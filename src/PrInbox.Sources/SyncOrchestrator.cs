using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PrInbox.Core.Models;
using PrInbox.Core.Storage;
using PrInbox.Sources;

namespace PrInbox.Sources;

/// <summary>
/// Orchestrates a single sync run for one source. Reads the source's review
/// inbox, fetches detail/threads per PR, and writes the registry. Records a
/// <c>sync_runs</c> row from start to finish.
/// </summary>
public sealed class SyncOrchestrator
{
    private readonly IPrReadSource _source;
    private readonly PullRequestRepository _pullRequests;
    private readonly PrSnapshotRepository _snapshots;
    private readonly ObservedThreadRepository _threads;
    private readonly SyncRunRepository _syncRuns;
    private readonly ILogger<SyncOrchestrator> _logger;

    public SyncOrchestrator(
        IPrReadSource source,
        PullRequestRepository pullRequests,
        PrSnapshotRepository snapshots,
        ObservedThreadRepository threads,
        SyncRunRepository syncRuns,
        ILogger<SyncOrchestrator>? logger = null)
    {
        _source = source;
        _pullRequests = pullRequests;
        _snapshots = snapshots;
        _threads = threads;
        _syncRuns = syncRuns;
        _logger = logger ?? NullLogger<SyncOrchestrator>.Instance;
    }

    public async Task<SyncResult> RunAsync(string identityUsed, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        var runId = await _syncRuns.StartAsync(_source.SourceId, identityUsed, ct);
        var syncedAt = DateTimeOffset.UtcNow;
        var prsSeen = 0;
        var prsFailed = 0;
        SyncRunStatus finalStatus = SyncRunStatus.Failed;
        string? finalError = null;

        var seenIdentities = new HashSet<string>();

        try
        {
            progress?.Report(new SyncProgress(_source.SourceId, "Fetching inbox", 0, null));
            var inbox = await _source.GetReviewInboxAsync(ct);
            progress?.Report(new SyncProgress(_source.SourceId, $"Inbox: {inbox.Count} PR(s)", 0, inbox.Count));

            foreach (var pr in inbox)
            {
                ct.ThrowIfCancellationRequested();
                seenIdentities.Add(pr.Identity.Display);
                progress?.Report(new SyncProgress(_source.SourceId, $"#{pr.Number} {pr.DisplayRepo}", prsSeen, inbox.Count));

                try
                {
                    await SyncOnePullRequestAsync(pr, identityUsed, syncedAt, ct);
                    prsSeen++;
                }
                catch (Exception ex)
                {
                    prsFailed++;
                    _logger.LogWarning(ex, "Failed to sync PR {Pr}: {Message}", pr.Identity.Display, ex.Message);
                }
            }

            await ReconcileMissingPrsAsync(_source.SourceId, seenIdentities, ct);

            finalStatus = prsFailed == 0
                ? SyncRunStatus.Ok
                : (prsFailed < prsSeen ? SyncRunStatus.Partial : SyncRunStatus.Failed);
        }
        catch (OperationCanceledException)
        {
            finalStatus = SyncRunStatus.Failed;
            finalError = "Cancelled.";
            throw;
        }
        catch (Exception ex)
        {
            finalStatus = SyncRunStatus.Failed;
            finalError = $"{ex.GetType().Name}: {ex.Message}";
            _logger.LogError(ex, "Sync run {RunId} failed for {SourceId}.", runId, _source.SourceId);
        }
        finally
        {
            try
            {
                await _syncRuns.CompleteAsync(runId, finalStatus, prsSeen, finalError, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to finalize sync_run {RunId}.", runId);
            }
        }

        return new SyncResult(runId, _source.SourceId, finalStatus, prsSeen, prsFailed, finalError);
    }

    private async Task SyncOnePullRequestAsync(RemotePullRequest pr, string identityUsed, DateTimeOffset syncedAt, CancellationToken ct)
    {
        var existing = await _pullRequests.GetAsync(pr.Identity.Display, ct);

        var row = new PullRequestRow(
            Identity: pr.Identity,
            SourceKind: pr.SourceKind,
            SourceId: pr.SourceId,
            DisplayRepo: pr.DisplayRepo,
            Number: pr.Number,
            Title: pr.Title,
            AuthorLogin: pr.AuthorLogin,
            Url: pr.Url,
            Status: pr.Status,
            TrackingReason: existing?.TrackingReason == TrackingReason.PreviouslyAssigned
                ? TrackingReason.Assigned
                : (existing?.TrackingReason ?? TrackingReason.Assigned),
            IdentityUsed: identityUsed,
            FirstSeenAt: existing?.FirstSeenAt ?? syncedAt,
            LastSyncedAt: syncedAt,
            LastBriefedHeadSha: existing?.LastBriefedHeadSha,
            LastReviewRunHeadSha: existing?.LastReviewRunHeadSha,
            LastPostedReviewHeadSha: existing?.LastPostedReviewHeadSha);

        await _pullRequests.UpsertAsync(row, ct);

        var detail = await _source.GetPullRequestDetailAsync(pr.Identity, ct);
        await _snapshots.InsertIfChangedAsync(
            pr.Identity, syncedAt,
            detail.HeadSha, detail.BaseSha, detail.MergeBaseSha,
            detail.OrderedCommitShas, detail.ReviewerState, detail.Status,
            detail.RawMetadataJson, ct);

        var threads = await _source.GetThreadsAsync(pr.Identity, ct);
        await _threads.UpsertManyAsync(pr.Identity, threads, syncedAt, ct);
    }

    private async Task ReconcileMissingPrsAsync(string sourceId, HashSet<string> seen, CancellationToken ct)
    {
        var allActive = await _pullRequests.ListActiveAsync(ct);
        foreach (var row in allActive)
        {
            if (row.SourceId != sourceId) continue;
            if (seen.Contains(row.Identity.Display)) continue;
            if (row.TrackingReason == TrackingReason.Assigned)
            {
                await _pullRequests.MarkPreviouslyAssignedAsync(row.Identity.Display, ct);
            }
        }
    }
}

public sealed record SyncProgress(string SourceId, string Message, int PrsSeen, int? PrsTotal);

public sealed record SyncResult(long RunId, string SourceId, SyncRunStatus Status, int PrsSeen, int PrsFailed, string? Error);
