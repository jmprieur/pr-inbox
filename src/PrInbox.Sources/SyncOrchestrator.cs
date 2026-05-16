using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PrInbox.Core.Models;
using PrInbox.Core.Storage;
using PrInbox.Sources;

namespace PrInbox.Sources;

/// <summary>
/// Orchestrates a sync run for a single source. Supports three modes:
/// <list type="bullet">
///   <item><see cref="RunFastAsync"/> — tier-2 only: list PRs and upsert
///         minimal rows. Cheap and streamable.</item>
///   <item><see cref="RunEnrichAsync"/> — tier-3 only: for already-listed
///         rows in <see cref="EnrichState.Basic"/>, fetch detail+threads and
///         persist them.</item>
///   <item><see cref="RunAsync"/> — the legacy/default: fast then enrich,
///         in sequence.</item>
/// </list>
/// Each call writes one <c>sync_runs</c> row from start to finish.
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

    /// <summary>
    /// Default: fast pass followed by enrich pass. Returns a single combined
    /// <see cref="SyncResult"/>.
    /// </summary>
    public async Task<SyncResult> RunAsync(string identityUsed, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        var fast = await RunFastAsync(identityUsed, progress, ct);
        if (fast.Status == SyncRunStatus.Failed)
        {
            return fast;
        }
        var enrich = await RunEnrichAsync(identityUsed, progress, ct);

        // Combine: prefer the more pessimistic status, sum counts, surface
        // the first error if any. Use the enrich run's runId (last writer).
        var combinedStatus =
            fast.Status == SyncRunStatus.Failed || enrich.Status == SyncRunStatus.Failed ? SyncRunStatus.Failed :
            fast.Status == SyncRunStatus.Partial || enrich.Status == SyncRunStatus.Partial ? SyncRunStatus.Partial :
            SyncRunStatus.Ok;
        return new SyncResult(
            RunId: enrich.RunId,
            SourceId: enrich.SourceId,
            Status: combinedStatus,
            PrsSeen: fast.PrsSeen,
            PrsFailed: fast.PrsFailed + enrich.PrsFailed,
            Error: fast.Error ?? enrich.Error);
    }

    /// <summary>
    /// Tier-2 fast pass: list PRs from the source and upsert minimal rows.
    /// Sets <see cref="EnrichState.Basic"/> on rows that are new or whose
    /// upstream <c>LastUpdated</c> moved past the existing
    /// <c>LastSyncedAt</c>. Reconciles missing PRs only after the listing
    /// stream completes successfully.
    /// </summary>
    public async Task<SyncResult> RunFastAsync(string identityUsed, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        var runId = await _syncRuns.StartAsync(_source.SourceId, identityUsed, ct);
        var syncedAt = DateTimeOffset.UtcNow;
        var prsSeen = 0;
        var prsFailed = 0;
        SyncRunStatus finalStatus = SyncRunStatus.Failed;
        string? finalError = null;
        var seenIdentities = new HashSet<string>();
        var listingCompleted = false;

        try
        {
            progress?.Report(new SyncProgress(_source.SourceId, "Fetching inbox", 0, null));

            await foreach (var pr in _source.ListAssignedFastAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                seenIdentities.Add(pr.Identity.Url);
                progress?.Report(new SyncProgress(_source.SourceId, $"#{pr.Number} {pr.DisplayRepo}", prsSeen, null));

                try
                {
                    await UpsertFastAsync(pr, identityUsed, syncedAt, ct);
                    prsSeen++;
                }
                catch (Exception ex)
                {
                    prsFailed++;
                    _logger.LogWarning(ex, "Fast-upsert failed for {Pr}: {Message}", pr.Identity.Url, ex.Message);
                }
            }

            listingCompleted = true;
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
            _logger.LogError(ex, "Fast sync run {RunId} failed for {SourceId} (listingCompleted={Completed}).",
                runId, _source.SourceId, listingCompleted);
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

    /// <summary>
    /// Tier-3 enrichment pass: for each row already listed by this source
    /// with <see cref="EnrichState.Basic"/>, fetch detail+threads and
    /// persist them. Candidates are scoped via <c>pr_source_bindings</c> so
    /// the runtime never tries to enrich a PR its identity cannot see.
    /// </summary>
    public async Task<SyncResult> RunEnrichAsync(string identityUsed, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        var runId = await _syncRuns.StartAsync(_source.SourceId, identityUsed, ct);
        var prsSeen = 0;
        var prsFailed = 0;
        SyncRunStatus finalStatus = SyncRunStatus.Failed;
        string? finalError = null;

        try
        {
            var candidates = await _pullRequests.ListNeedingEnrichmentAsync(_source.SourceId, identityUsed, ct);
            progress?.Report(new SyncProgress(_source.SourceId, $"Enriching: {candidates.Count} PR(s)", 0, candidates.Count));

            string? firstError = null;
            foreach (var row in candidates)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new SyncProgress(_source.SourceId, $"#{row.Number} {row.DisplayRepo}", prsSeen, candidates.Count));

                try
                {
                    await EnrichOneAsync(row, ct);
                    prsSeen++;
                }
                catch (Exception ex)
                {
                    prsFailed++;
                    firstError ??= $"{ex.GetType().Name}: {ex.Message}";
                    _logger.LogWarning(ex, "Enrich failed for {Pr}: {Message}", row.Identity.Url, ex.Message);
                }
            }

            finalStatus = prsFailed == 0
                ? SyncRunStatus.Ok
                : (prsFailed < (prsSeen + prsFailed) ? SyncRunStatus.Partial : SyncRunStatus.Failed);
            if (finalError is null && firstError is not null)
            {
                finalError = firstError;
            }
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
            _logger.LogError(ex, "Enrich sync run {RunId} failed for {SourceId}.", runId, _source.SourceId);
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

    private async Task UpsertFastAsync(RemotePullRequest pr, string identityUsed, DateTimeOffset syncedAt, CancellationToken ct)
    {
        var existing = await _pullRequests.GetAsync(pr.Identity.Url, ct);

        // Downgrade enrich_state to Basic when the row is new OR upstream
        // changed since we last enriched. Otherwise preserve existing state
        // so freshly-enriched rows aren't unnecessarily marked stale.
        var needsEnrichment = existing is null || pr.LastUpdated > existing.LastSyncedAt;
        var enrichState = needsEnrichment
            ? EnrichState.Basic
            : existing!.EnrichState;

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
            EnrichState: enrichState,
            LastBriefedHeadSha: existing?.LastBriefedHeadSha,
            LastReviewRunHeadSha: existing?.LastReviewRunHeadSha,
            LastPostedReviewHeadSha: existing?.LastPostedReviewHeadSha);

        await _pullRequests.UpsertAsync(row, ct);
    }

    private async Task EnrichOneAsync(PullRequestRow row, CancellationToken ct)
    {
        var enrichedAt = DateTimeOffset.UtcNow;
        var bundle = await _source.EnrichAsync(row.Identity, ct);

        await _snapshots.InsertIfChangedAsync(
            row.Identity, enrichedAt,
            bundle.Detail.HeadSha, bundle.Detail.BaseSha, bundle.Detail.MergeBaseSha,
            bundle.Detail.OrderedCommitShas, bundle.Detail.ReviewerState, bundle.Detail.Status,
            bundle.Detail.RawMetadataJson, ct);

        await _threads.UpsertManyAsync(row.Identity, bundle.Threads, enrichedAt, ct);
        await _pullRequests.MarkEnrichedAsync(row.Identity.Url, ct);
    }

    private async Task ReconcileMissingPrsAsync(string sourceId, HashSet<string> seen, CancellationToken ct)
    {
        var allActive = await _pullRequests.ListActiveAsync(ct);
        foreach (var row in allActive)
        {
            if (row.SourceId != sourceId) continue;
            if (seen.Contains(row.Identity.Url)) continue;
            if (row.TrackingReason == TrackingReason.Assigned)
            {
                await _pullRequests.MarkPreviouslyAssignedAsync(row.Identity.Url, ct);
            }
        }
    }
}

public sealed record SyncProgress(string SourceId, string Message, int PrsSeen, int? PrsTotal);

public sealed record SyncResult(long RunId, string SourceId, SyncRunStatus Status, int PrsSeen, int PrsFailed, string? Error);
