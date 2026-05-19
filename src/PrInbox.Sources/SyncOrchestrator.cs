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

        return new SyncResult(runId, _source.SourceId, finalStatus, prsSeen, prsFailed, finalError, seenIdentities);
    }

    /// <summary>
    /// Re-evaluates PRs that we still consider <c>status='open'</c> for the
    /// given (source, identity) but which did <em>not</em> show up in the
    /// most recent fast pass (the &quot;disappeared&quot; set). Calls
    /// <see cref="IPrReadSource.EnrichAsync"/> on up to <paramref name="cap"/>
    /// of them: the new status is persisted to <c>pull_requests.status</c>,
    /// and rows that remain <c>open</c> have <c>disappeared_at</c> stamped
    /// so the UI can hide them as "no longer in your queue".
    /// </summary>
    public async Task RunDisappearedSweepAsync(
        string identityUsed,
        IReadOnlyCollection<string> seenUrls,
        int cap,
        CancellationToken ct)
    {
        if (cap <= 0) return;
        var seen = seenUrls as ISet<string> ?? new HashSet<string>(seenUrls, StringComparer.OrdinalIgnoreCase);
        var dbOpen = await _pullRequests.ListOpenUrlsByIdentityAsync(_source.SourceId, identityUsed, ct);
        var disappeared = dbOpen.Where(u => !seen.Contains(u)).Take(cap).ToList();
        if (disappeared.Count == 0)
        {
            _logger.LogDebug("Disappeared sweep ({SourceId}/{Identity}): no candidates.",
                _source.SourceId, identityUsed);
            return;
        }
        _logger.LogInformation("Disappeared sweep ({SourceId}/{Identity}): re-enriching {Count} PR(s).",
            _source.SourceId, identityUsed, disappeared.Count);
        foreach (var url in disappeared)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var row = await _pullRequests.GetAsync(url, ct);
                if (row is null) continue;
                await EnrichOneAsync(row, ct);
                // EnrichOneAsync just persisted any new status. Re-read the row.
                var updated = await _pullRequests.GetAsync(url, ct);
                if (updated is not null && updated.Status == PullRequestStatus.Open)
                {
                    await _pullRequests.SetDisappearedAtAsync(url, DateTimeOffset.UtcNow, ct);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Common case: the user no longer has access to the PR.
                // Mark inaccessible so the UI can stop tracking it.
                _logger.LogDebug(ex, "Disappeared-sweep enrich failed for {Url}; marking inaccessible.", url);
                try { await _pullRequests.MarkInaccessibleAsync(url, ct); } catch { }
            }
        }
    }

    /// <summary>
    /// Periodic verification sweep: re-enrich up to <paramref name="n"/>
    /// of the open, non-ignored rows owned by (source, identity), starting
    /// with the ones whose <c>last_swept_at</c> is oldest. Catches PRs
    /// whose state changed without ever leaving the fast-sync result set
    /// (e.g. merged-and-immediately-reopened, or status drift on ADO).
    /// </summary>
    public async Task RunTtlSweepAsync(string identityUsed, int n, CancellationToken ct)
    {
        if (n <= 0) return;
        var candidates = await _pullRequests.ListOldestSweptOpenAsync(
            _source.SourceId, identityUsed, n, ct);
        if (candidates.Count == 0) return;
        _logger.LogDebug("TTL sweep ({SourceId}/{Identity}): re-enriching {Count} PR(s).",
            _source.SourceId, identityUsed, candidates.Count);
        var now = DateTimeOffset.UtcNow;
        foreach (var row in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await EnrichOneAsync(row, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "TTL-sweep enrich failed for {Url}.", row.Identity.Url);
            }
            try { await _pullRequests.MarkSweptAsync(row.Identity.Url, now, ct); } catch { }
        }
    }

    /// <summary>
    /// Tier-3 enrichment pass: for each row already listed by this source
    /// with <see cref="EnrichState.Basic"/>, fetch detail+threads and
    /// persist them. Candidates are scoped via <c>pr_source_bindings</c> so
    /// the runtime never tries to enrich a PR its identity cannot see.
    /// </summary>
    /// <param name="precomputedCandidates">
    /// If non-null, used directly instead of fetching via
    /// <see cref="PullRequestRepository.ListNeedingEnrichmentAsync"/>. Lets
    /// callers split one DB-side candidate set into priority tiers
    /// (e.g. UI-visible PRs first, hidden PRs second) without re-querying.
    /// </param>
    /// <param name="tierLabel">
    /// Optional progress label (e.g. <c>"visible"</c>, <c>"background"</c>).
    /// Appears in progress messages so the UI can distinguish staged passes.
    /// </param>
    public async Task<SyncResult> RunEnrichAsync(
        string identityUsed,
        IProgress<SyncProgress>? progress,
        CancellationToken ct,
        IReadOnlyList<PullRequestRow>? precomputedCandidates = null,
        string? tierLabel = null)
    {
        var runId = await _syncRuns.StartAsync(_source.SourceId, identityUsed, ct);
        var prsSeen = 0;
        var prsFailed = 0;
        SyncRunStatus finalStatus = SyncRunStatus.Failed;
        string? finalError = null;

        try
        {
            var candidates = precomputedCandidates ?? await _pullRequests.ListNeedingEnrichmentAsync(
                _source.SourceId, identityUsed,
                minDossierVersion: PrInbox.Core.Reviewing.BriefService.CurrentDossierVersion,
                ct);
            var label = string.IsNullOrEmpty(tierLabel) ? "Enriching" : $"Enriching ({tierLabel})";
            progress?.Report(new SyncProgress(_source.SourceId, $"{label}: {candidates.Count} PR(s)", 0, candidates.Count));

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
            LastPostedReviewHeadSha: existing?.LastPostedReviewHeadSha,
            // Note: ADO's PR list endpoint doesn't expose lastUpdatedDate at
            // the PR level, so pr.LastUpdated is CreationDate for ADO sources
            // and pr.UpdatedAt for GitHub. The UI's "Recent" sort treats this
            // as best-available activity signal and ranks NULL last.
            LastUpstreamUpdatedAt: pr.LastUpdated);

        await _pullRequests.UpsertAsync(row, ct);
    }

    /// <summary>
    /// Forced single-PR enrichment: bypasses
    /// <see cref="PullRequestRepository.ListNeedingEnrichmentAsync"/> so it
    /// also re-enriches PRs that the candidate filter would skip (e.g.
    /// rows already at the current dossier version but whose threads were
    /// captured before a schema bump added a column). Returns the URL of
    /// the row enriched, or <c>null</c> if the URL is not tracked by this
    /// source / identity binding. Throws on enrichment failure.
    /// </summary>
    public async Task<string?> RunEnrichOneAsync(string prUrl, CancellationToken ct)
    {
        var row = await _pullRequests.GetAsync(prUrl, ct);
        if (row is null) return null;

        // Only this source's binding is allowed to enrich this row.
        if (!string.Equals(row.SourceId, _source.SourceId, StringComparison.Ordinal))
        {
            return null;
        }

        var runId = await _syncRuns.StartAsync(_source.SourceId, row.IdentityUsed, ct);
        try
        {
            await EnrichOneAsync(row, ct);
            await _syncRuns.CompleteAsync(runId, SyncRunStatus.Ok, 1, null, CancellationToken.None);
            return row.Identity.Url;
        }
        catch (Exception ex)
        {
            await _syncRuns.CompleteAsync(runId, SyncRunStatus.Failed, 0, $"{ex.GetType().Name}: {ex.Message}", CancellationToken.None);
            throw;
        }
    }

    private async Task EnrichOneAsync(PullRequestRow row, CancellationToken ct)
    {
        var enrichedAt = DateTimeOffset.UtcNow;
        var bundle = await _source.EnrichAsync(row.Identity, ct);

        var files = bundle.Detail.Files?
            .Select(f => new SnapshotFileChange(f.Path, f.Additions, f.Deletions, f.Status))
            .ToList();

        var inserted = await _snapshots.InsertIfChangedAsync(
            row.Identity, enrichedAt,
            bundle.Detail.HeadSha, bundle.Detail.BaseSha, bundle.Detail.MergeBaseSha,
            bundle.Detail.OrderedCommitShas, bundle.Detail.ReviewerState, bundle.Detail.Status,
            bundle.Detail.RawMetadataJson, ct,
            mergeableState: bundle.Detail.MergeableState,
            ciStatus: bundle.Detail.CiStatus,
            files: files);

        if (!inserted)
        {
            // Canonical state unchanged → dedup blocked the insert, but dossier
            // metadata (CI, mergeable, files) may have moved. Refresh the
            // latest snapshot's dossier columns in place so the brief sees the
            // freshest data without spamming the append-only history.
            await _snapshots.UpdateLatestDossierAsync(
                row.Identity,
                mergeableState: bundle.Detail.MergeableState,
                ciStatus: bundle.Detail.CiStatus,
                files: files,
                ct);
        }

        await _threads.UpsertManyAsync(row.Identity, bundle.Threads, enrichedAt, ct);
        await _pullRequests.MarkEnrichedAsync(row.Identity.Url, ct);

        // Persist PR body separately. The fast-pass UpsertAsync runs without
        // a body; the enrich pass is the only place we have one to write.
        if (!string.IsNullOrEmpty(bundle.Detail.Body))
        {
            await _pullRequests.UpdateBodyAsync(row.Identity.Url, bundle.Detail.Body, ct);
        }

        // Stamp the dossier-schema version this enrich satisfied so the
        // backfill SELECT stops re-electing this PR.
        await _pullRequests.UpdateDossierVersionAsync(
            row.Identity.Url,
            PrInbox.Core.Reviewing.BriefService.CurrentDossierVersion,
            ct);

        // Propagate the latest platform status to pull_requests.status so
        // PRs that have been merged/closed since the last fast pass don't
        // remain stuck at 'open' in the inbox.
        if (bundle.Detail.Status != row.Status)
        {
            await _pullRequests.UpdateStatusAsync(row.Identity.Url, bundle.Detail.Status, ct);
        }
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

        // Clear stale disappeared_at for PRs that reappeared in this fast
        // pass. (If a row was previously stamped, but it now shows up in
        // the list again, the user is once more a requested reviewer.)
        foreach (var url in seen)
        {
            try { await _pullRequests.SetDisappearedAtAsync(url, null, ct); } catch { }
        }
    }
}

public sealed record SyncProgress(string SourceId, string Message, int PrsSeen, int? PrsTotal);

/// <summary>
/// Outcome of a single sync run. <see cref="SeenUrls"/> is populated only
/// by <see cref="SyncOrchestrator.RunFastAsync"/>; callers can diff it
/// against the DB's known-open URLs to drive the disappeared sweep.
/// </summary>
public sealed record SyncResult(
    long RunId,
    string SourceId,
    SyncRunStatus Status,
    int PrsSeen,
    int PrsFailed,
    string? Error,
    IReadOnlyCollection<string>? SeenUrls = null);
