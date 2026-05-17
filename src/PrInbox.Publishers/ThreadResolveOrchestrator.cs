using Microsoft.Extensions.Logging;
using PrInbox.Core.Storage;

namespace PrInbox.Publishers;

/// <summary>
/// Outcome of a <see cref="ThreadResolveOrchestrator.ResolveAsync"/> call.
/// Strictly richer than the publisher-layer
/// <see cref="ThreadResolveResult"/> — adds local DB write-back state and
/// the list of node ids that were dropped because the inbox doesn't know
/// them (defensive server-side validation against the caller's selection).
/// </summary>
/// <param name="PublisherResult">
/// Per-thread outcomes from the publisher. <c>null</c> when validation
/// failed before any publisher call.
/// </param>
/// <param name="UnknownNodeIds">
/// Requested ids the inbox does not have on record for this PR. Either
/// already resolved (and pruned from the open-threads view) or
/// fabricated by the client. We do not call the publisher for these.
/// </param>
/// <param name="LocalRowsMarked">
/// Number of <c>observed_threads</c> rows whose <c>resolved_at</c> was
/// stamped as a result of this call. Includes rows shared by N comments
/// of one GraphQL thread (1 mutation → N row updates).
/// </param>
/// <param name="DryRun">Whether the orchestrator ran in dry-run mode.</param>
/// <param name="Errors">
/// Errors that prevented orchestration from starting (PR not in inbox,
/// publisher selection failed, etc.) OR errors bubbled up from the
/// publisher.
/// </param>
public sealed record ThreadResolveOrchestratorResult(
    ThreadResolveResult? PublisherResult,
    IReadOnlyList<string> UnknownNodeIds,
    int LocalRowsMarked,
    bool DryRun,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public static ThreadResolveOrchestratorResult Failure(string error) =>
        new(
            PublisherResult: null,
            UnknownNodeIds: Array.Empty<string>(),
            LocalRowsMarked: 0,
            DryRun: false,
            Warnings: Array.Empty<string>(),
            Errors: new[] { error });
}

/// <summary>
/// Drives the bulk thread-resolve flow end-to-end:
/// <list type="number">
///   <item>Validate the caller's selection against the local DB (the inbox
///         knows which thread node ids exist on this PR — never trust the
///         browser).</item>
///   <item>Select the publisher for the PR's recorded identity.</item>
///   <item>Call <see cref="IPrReviewPublisher.ResolveThreadsAsync"/>.</item>
///   <item>On success (non-dry-run), stamp local
///         <c>observed_threads.resolved_at</c> for ids the publisher
///         reported resolved OR already resolved upstream — both are
///         legitimate "stop showing this in the inbox" signals.</item>
/// </list>
/// Serialises calls per-PR (the per-PR semaphore in
/// <see cref="ReviewPublishOrchestrator"/> is duplicated here intentionally
/// — different lock dictionary; a publish and a resolve on the same PR
/// don't conflict at the data level).
/// </summary>
public sealed class ThreadResolveOrchestrator
{
    private readonly IPublisherSelector _selector;
    private readonly PullRequestRepository _prRepo;
    private readonly ObservedThreadRepository _threadRepo;
    private readonly ILogger<ThreadResolveOrchestrator> _log;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _locks =
        new(StringComparer.Ordinal);

    public ThreadResolveOrchestrator(
        IPublisherSelector selector,
        PullRequestRepository prRepo,
        ObservedThreadRepository threadRepo,
        ILogger<ThreadResolveOrchestrator> log)
    {
        _selector = selector;
        _prRepo = prRepo;
        _threadRepo = threadRepo;
        _log = log;
    }

    public async Task<ThreadResolveOrchestratorResult> ResolveAsync(
        string prUrl,
        IReadOnlyList<string> requestedThreadNodeIds,
        bool dryRun,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prUrl))
        {
            return ThreadResolveOrchestratorResult.Failure("prUrl is required.");
        }
        if (requestedThreadNodeIds.Count == 0)
        {
            return ThreadResolveOrchestratorResult.Failure("No threads selected.");
        }

        var prRow = await _prRepo.GetAsync(prUrl, ct);
        if (prRow is null)
        {
            return ThreadResolveOrchestratorResult.Failure(
                $"PR {prUrl} not found in inbox; sync the inbox first.");
        }

        IPrReviewPublisher publisher;
        try
        {
            publisher = _selector.SelectFor(prUrl, prRow.IdentityUsed);
        }
        catch (Exception ex)
        {
            return ThreadResolveOrchestratorResult.Failure(
                $"Cannot select a publisher for {prUrl}: {ex.Message}");
        }

        var gate = _locks.GetOrAdd(prUrl, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Load the inbox's authoritative open-thread set. Anything the
            // caller asks about that isn't here is either already resolved
            // (and we shouldn't bother the platform) or unknown (browser
            // tampering or stale state). We collect them as a warning and
            // do not include them in the publisher request.
            var openThreads = await _threadRepo.GetOpenThreadsAsync(prRow.Identity, ct);
            var knownNodeIds = openThreads
                .Where(t => !string.IsNullOrEmpty(t.PlatformThreadNodeId))
                .Select(t => t.PlatformThreadNodeId!)
                .ToHashSet(StringComparer.Ordinal);

            var validatedIds = new List<string>();
            var unknownIds = new List<string>();
            foreach (var id in requestedThreadNodeIds.Distinct(StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (knownNodeIds.Contains(id)) validatedIds.Add(id);
                else unknownIds.Add(id);
            }

            if (validatedIds.Count == 0)
            {
                var msg = unknownIds.Count > 0
                    ? "None of the supplied thread ids are currently open on this PR."
                    : "No valid thread ids supplied.";
                return new ThreadResolveOrchestratorResult(
                    PublisherResult: null,
                    UnknownNodeIds: unknownIds,
                    LocalRowsMarked: 0,
                    DryRun: dryRun,
                    Warnings: Array.Empty<string>(),
                    Errors: new[] { msg });
            }

            var pubResult = await publisher.ResolveThreadsAsync(
                new ThreadResolveRequest(prUrl, validatedIds, dryRun),
                ct);

            var rowsMarked = 0;
            if (!dryRun && pubResult.Performed)
            {
                // Both "we just resolved it" and "it was already resolved
                // upstream" mean "do not show this in the inbox anymore".
                var idsToMark = pubResult.ResolvedNodeIds
                    .Concat(pubResult.AlreadyResolvedNodeIds)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (idsToMark.Count > 0)
                {
                    try
                    {
                        rowsMarked = await _threadRepo.MarkResolvedByNodeIdsAsync(
                            prRow.Identity, idsToMark, DateTimeOffset.UtcNow, ct);
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex,
                            "Resolved {N} thread(s) upstream but FAILED to mark them locally for {Url}",
                            idsToMark.Count, prUrl);
                        return new ThreadResolveOrchestratorResult(
                            PublisherResult: pubResult,
                            UnknownNodeIds: unknownIds,
                            LocalRowsMarked: 0,
                            DryRun: false,
                            Warnings: new[]
                            {
                                $"Upstream resolved but local DB update failed: {ex.Message}. " +
                                "The next sync will reconcile.",
                            },
                            Errors: Array.Empty<string>());
                    }
                }
            }

            return new ThreadResolveOrchestratorResult(
                PublisherResult: pubResult,
                UnknownNodeIds: unknownIds,
                LocalRowsMarked: rowsMarked,
                DryRun: dryRun,
                Warnings: Array.Empty<string>(),
                Errors: Array.Empty<string>());
        }
        finally
        {
            gate.Release();
        }
    }
}
