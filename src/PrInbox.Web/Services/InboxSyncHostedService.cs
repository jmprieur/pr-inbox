using PrInbox.Core.Config;
using PrInbox.Core.Credentials;
using PrInbox.Core.Models;
using PrInbox.Core.Storage;
using PrInbox.Sources;

namespace PrInbox.Web.Services;

/// <summary>
/// Background sync loop. On startup: load cached rows from SQLite
/// (tier 1), kick a fast sync (tier 2) and an enrich pass (tier 3),
/// then re-sync on an interval. Pushes everything through
/// <see cref="InboxState"/>.
/// </summary>
public sealed class InboxSyncHostedService : BackgroundService
{
    private readonly InboxState _state;
    private readonly IConfiguration _config;
    private readonly ILogger<InboxSyncHostedService> _log;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private volatile bool _syncing;
    private int _configChangedFlag;

    public InboxSyncHostedService(InboxState state, IConfiguration config, ILogger<InboxSyncHostedService> log)
    {
        _state = state;
        _config = config;
        _log = log;
    }

    /// <summary>True while a fast+enrich pass is running (either automatic or manual).</summary>
    public bool IsSyncing => _syncing;

    /// <summary>
    /// Marks that configuration changed (e.g. a source was added/removed
    /// via the Settings page). Consumed by <see cref="ConsumeConfigChanged"/>
    /// on the next Inbox arrival so the inbox can kick an out-of-band
    /// sync rather than waiting up to one full interval for the
    /// background loop. Atomic; safe to call from any thread.
    /// </summary>
    public void NoteConfigChanged() => Interlocked.Exchange(ref _configChangedFlag, 1);

    /// <summary>
    /// Returns and clears the config-changed flag in a single atomic
    /// operation. Returns <c>true</c> exactly once per
    /// <see cref="NoteConfigChanged"/> call, regardless of how many
    /// readers call it.
    /// </summary>
    public bool ConsumeConfigChanged() => Interlocked.Exchange(ref _configChangedFlag, 0) == 1;

    /// <summary>
    /// Run an out-of-band sync immediately. Serialized with the
    /// background loop via <see cref="_syncGate"/>; concurrent calls
    /// return without queueing extra work.
    /// </summary>
    public async Task<bool> TriggerNowAsync(CancellationToken ct)
    {
        if (!await _syncGate.WaitAsync(0, ct))
        {
            return false; // already running
        }
        try
        {
            _syncing = true;
            _state.NoteSync("Manual refresh started...");
            await RunSyncIterationAsync(ct);
            return true;
        }
        finally
        {
            _syncing = false;
            _syncGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSec = _config.GetValue<int?>("PrInbox:SyncIntervalSeconds") ?? 300;
        var runOnStartup = _config.GetValue<bool?>("PrInbox:FastSyncOnStartup") ?? true;

        // Tier 1 — read cache so the page renders immediately.
        try
        {
            await RefreshFromCacheAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Initial cache load failed");
        }

        if (runOnStartup)
        {
            await TryGatedSyncAsync(stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(intervalSec), stoppingToken); }
            catch (OperationCanceledException) { return; }

            await TryGatedSyncAsync(stoppingToken);
        }
    }

    private async Task TryGatedSyncAsync(CancellationToken ct)
    {
        // If a manual refresh is currently running, skip this tick rather
        // than queueing — the manual run will refresh the inbox anyway.
        if (!await _syncGate.WaitAsync(0, ct))
        {
            _log.LogDebug("Skipping scheduled sync; another sync is in progress.");
            return;
        }
        try
        {
            _syncing = true;
            await RunSyncIterationAsync(ct);
        }
        finally
        {
            _syncing = false;
            _syncGate.Release();
        }
    }

    private async Task RunSyncIterationAsync(CancellationToken ct)
    {
        try
        {
            var fastFailures = await RunFastSyncAsync(ct);
            await RefreshFromCacheAsync(ct);
            var enrichFailures = await RunEnrichSyncAsync(ct);
            await RefreshFromCacheAsync(ct);

            var totalFailures = fastFailures + enrichFailures;
            var suffix = totalFailures > 0
                ? $" ({totalFailures} source(s) failed; see logs)"
                : "";
            _state.NoteSync($"Synced at {DateTimeOffset.Now:HH:mm:ss}{suffix}");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sync iteration failed");
            _state.NoteSync($"Sync failed: {ex.Message}");
        }
    }

    private async Task RefreshFromCacheAsync(CancellationToken ct)
    {
        var (prRepo, threadRepo, snapRepo, _) = OpenFullRepos();
        var prs = await prRepo.ListAllAsync(ct);

        var rows = new List<InboxRow>(prs.Count);
        foreach (var pr in prs)
        {
            var (open, bot, likelyDone) = await CountThreadsAsync(threadRepo, pr.Identity, ct);
            var snap = await snapRepo.GetLatestAsync(pr.Identity, ct);
            var drift = DriftInfo.Compute(pr, snap);
            rows.Add(InboxRow.FromRow(pr, open, bot, drift, likelyDone));
        }
        _state.ReplaceAll(rows);
    }

    /// <summary>
    /// Runs the tier-2 fast pass + disappeared sweep across every runtime.
    /// Sources run in parallel — each source spends most of its time on
    /// paginated REST calls to its remote, and SQLite WAL (enabled by the
    /// migration runner) plus per-connection <c>busy_timeout</c> let the
    /// concurrent writes serialize politely at the DB layer.
    /// </summary>
    /// <returns>
    /// Count of sources whose fast pass failed. A disappeared-sweep
    /// failure is logged at Warning but does NOT count toward this tally —
    /// the data pass itself succeeded; only the cleanup step failed. Used
    /// by <see cref="RunSyncIterationAsync"/> to surface a per-cycle
    /// failure count in the inbox banner.
    /// </returns>
    private async Task<int> RunFastSyncAsync(CancellationToken ct)
    {
        var config = await PrInboxConfig.LoadAsync(null);
        if (config.Sources.Count == 0 && config.Ado.Projects.Count == 0) return 0;

        var (prRepo, threadRepo, snapRepo, syncRunRepo) = OpenFullRepos();
        var runtimes = new SourceFactory().Build(config);

        return await RunFastSyncAsync(runtimes, prRepo, snapRepo, threadRepo, syncRunRepo, ct);
    }

    /// <summary>
    /// Testable overload: takes the runtime list directly so tests can
    /// inject fake sources (including barrier-coordinated fakes that
    /// prove parallel execution).
    /// </summary>
    internal async Task<int> RunFastSyncAsync(
        IReadOnlyList<RuntimeSource> runtimes,
        PullRequestRepository prRepo,
        PrSnapshotRepository snapRepo,
        ObservedThreadRepository threadRepo,
        SyncRunRepository syncRunRepo,
        CancellationToken ct)
    {
        if (runtimes.Count == 0) return 0;

        // Fan out: one task per runtime. Each task is fully independent —
        // it builds its own orchestrator, owns its own progress channel,
        // and either returns 0 (ok) or 1 (fast pass failed). The
        // disappeared sweep that follows the fast pass within the same
        // task is best-effort: a sweep failure logs Warning but does NOT
        // increment the tally because the actual data pass succeeded.
        //
        // Cancellation: OperationCanceledException is allowed to bubble
        // out of Task.WhenAll. We let the outer RunSyncIterationAsync
        // handle shutdown — we don't count a shutdown cancel as a source
        // failure.
        var tasks = runtimes.Select(rt =>
            RunOneFastAsync(rt, prRepo, snapRepo, threadRepo, syncRunRepo, _log, ct));
        var results = await Task.WhenAll(tasks);
        return results.Sum();
    }

    /// <summary>
    /// Runs the fast pass + disappeared sweep for a single runtime.
    /// </summary>
    /// <returns>
    /// <c>1</c> if the fast pass failed or threw a non-cancellation
    /// exception; <c>0</c> otherwise. A disappeared-sweep failure logs at
    /// Warning but still returns <c>0</c> because the data pass succeeded.
    /// </returns>
    internal static async Task<int> RunOneFastAsync(
        RuntimeSource rt,
        PullRequestRepository prRepo,
        PrSnapshotRepository snapRepo,
        ObservedThreadRepository threadRepo,
        SyncRunRepository syncRunRepo,
        ILogger log,
        CancellationToken ct)
    {
        try
        {
            var orchestrator = new SyncOrchestrator(rt.Source, prRepo, snapRepo, threadRepo, syncRunRepo);
            var progress = new Progress<SyncProgress>();
            var result = await orchestrator.RunFastAsync(rt.Identity, progress, ct);

            // Sweep A: anything we still think is open but the source
            // didn't return this pass. Cap protects rate limits. Sweep
            // failure does NOT count toward the source-failure tally —
            // the fast list itself succeeded; only the cleanup failed.
            if (result.SeenUrls is not null && result.Status != SyncRunStatus.Failed)
            {
                try
                {
                    await orchestrator.RunDisappearedSweepAsync(
                        rt.Identity, result.SeenUrls, cap: 20, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Disappeared sweep of {SourceId} failed", rt.Source.SourceId);
                }
            }

            return result.Status == SyncRunStatus.Failed ? 1 : 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown / user-initiated cancel — don't count as a source
            // failure. Let it propagate so Task.WhenAll faults
            // deterministically and the outer iteration short-circuits.
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Fast sync of {SourceId} failed", rt.Source.SourceId);
            return 1;
        }
    }

    /// <summary>
    /// Runs the tier-3 enrich pass across every runtime using a
    /// visible-first partition: PRs that pass the user's current
    /// <see cref="InboxFilters"/> are enriched before the hidden set, with
    /// a mid-cycle UI refresh in between so the dashboard reflects fresh
    /// visible data as quickly as possible.
    /// </summary>
    /// <returns>
    /// Count of failures: partition errors, per-source enrich failures,
    /// and the mid-cycle refresh (counted as +1 if it threw). Used by
    /// <see cref="RunSyncIterationAsync"/> to build the end-of-cycle
    /// banner.
    /// </returns>
    private async Task<int> RunEnrichSyncAsync(CancellationToken ct)
    {
        var config = await PrInboxConfig.LoadAsync(null);
        if (config.Sources.Count == 0 && config.Ado.Projects.Count == 0) return 0;

        var (prRepo, threadRepo, snapRepo, syncRunRepo) = OpenFullRepos();
        var runtimes = new SourceFactory().Build(config);

        var failures = 0;

        // Pin the filter snapshot to the START of the cycle. Filter changes
        // that happen mid-cycle apply on the *next* iteration — keeps both
        // passes evaluating against the same set so the visible/hidden
        // partition is consistent across all sources.
        InboxFilters filters;
        try
        {
            var prefsDb = new PrInboxDb(PrInboxDb.DefaultUserConnectionString());
            var prefsRepo = new UiPreferencesRepository(prefsDb);
            filters = await InboxFilters.LoadAsync(prefsRepo, config, ct);
        }
        catch (Exception ex)
        {
            // If we can't load filters, fall back to "everything is visible"
            // so the legacy single-pass behavior still runs.
            _log.LogWarning(ex, "Could not load inbox filters; running enrich without priority partitioning.");
            filters = InboxFilters.From(
                showClosed: true, showIgnored: true,
                enabledSources: InboxFilters.KnownSourceClasses,
                excludedRepos: Array.Empty<string>(),
                excludedAuthors: Array.Empty<string>(),
                ignoredRepoRegexes: Array.Empty<System.Text.RegularExpressions.Regex>());
        }

        // Partition each source's candidates ONCE: one DB query per source,
        // then visible/hidden split in memory.
        var partitions = new List<(RuntimeSource Runtime, SyncOrchestrator Orch,
                                   List<PullRequestRow> Visible, List<PullRequestRow> Hidden)>();
        foreach (var rt in runtimes)
        {
            if (ct.IsCancellationRequested) return failures;
            try
            {
                var orchestrator = new SyncOrchestrator(rt.Source, prRepo, snapRepo, threadRepo, syncRunRepo);
                var candidates = await prRepo.ListNeedingEnrichmentAsync(
                    rt.Source.SourceId, rt.Identity,
                    minDossierVersion: PrInbox.Core.Reviewing.BriefService.CurrentDossierVersion,
                    ct);
                var (visible, hidden) = PartitionCandidates(candidates, filters);
                partitions.Add((rt, orchestrator, visible, hidden));
            }
            catch (Exception ex)
            {
                failures++;
                _log.LogWarning(ex, "Building enrich partition for {SourceId} failed", rt.Source.SourceId);
            }
        }

        var totalVisible = partitions.Sum(p => p.Visible.Count);
        var totalHidden = partitions.Sum(p => p.Hidden.Count);

        // Pass 1: visible across every source. Done first so the rows the
        // user can see on the dashboard refresh as quickly as possible.
        foreach (var (rt, orch, visible, _) in partitions)
        {
            if (ct.IsCancellationRequested) return failures;
            if (visible.Count == 0) continue;
            try
            {
                var progress = new Progress<SyncProgress>();
                await orch.RunEnrichAsync(
                    rt.Identity, progress, ct,
                    precomputedCandidates: visible,
                    tierLabel: "visible");
            }
            catch (Exception ex)
            {
                failures++;
                _log.LogWarning(ex, "Visible-pass enrich of {SourceId} failed", rt.Source.SourceId);
            }
        }

        // Refresh InboxState NOW so the dashboard reflects fresh visible
        // data before the slower background pass starts. Without this, the
        // user wouldn't *see* the visible-first benefit — state is pushed
        // to the UI only after every enrich call has run.
        var midCycleRefreshed = false;
        if (totalVisible > 0)
        {
            try
            {
                await RefreshFromCacheAsync(ct);
                midCycleRefreshed = true;
            }
            catch (Exception ex)
            {
                failures++;
                // Bumped from LogDebug -> LogWarning so this is visible in
                // logs, and we now post an explicit banner. Previously the
                // user would see the "background syncing..." success
                // message even when the UI hadn't actually refreshed.
                _log.LogWarning(ex, "Mid-cycle UI refresh failed after visible enrich pass");
                _state.NoteSync(
                    $"Visible synced at {DateTimeOffset.Now:HH:mm:ss} but UI refresh failed: {ex.Message}");
            }

            if (midCycleRefreshed && totalHidden > 0)
            {
                _state.NoteSync($"Visible synced at {DateTimeOffset.Now:HH:mm:ss} · background syncing…");
            }
        }

        // Pass 2: hidden across every source.
        foreach (var (rt, orch, _, hidden) in partitions)
        {
            if (ct.IsCancellationRequested) return failures;
            if (hidden.Count == 0) continue;
            try
            {
                var progress = new Progress<SyncProgress>();
                await orch.RunEnrichAsync(
                    rt.Identity, progress, ct,
                    precomputedCandidates: hidden,
                    tierLabel: "background");
            }
            catch (Exception ex)
            {
                failures++;
                _log.LogWarning(ex, "Background-pass enrich of {SourceId} failed", rt.Source.SourceId);
            }
        }

        // TTL sweep per source. Lower priority than either enrich pass —
        // catches state drift on PRs that never leave the fast-sync result
        // set. Capped to N=10 per source so it doesn't dominate the cycle.
        // Sweep failures are logged loudly but don't count toward the
        // banner tally because the actual data pass already succeeded.
        foreach (var (rt, orch, _, _) in partitions)
        {
            if (ct.IsCancellationRequested) return failures;
            try
            {
                await orch.RunTtlSweepAsync(rt.Identity, n: 10, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "TTL sweep of {SourceId} failed", rt.Source.SourceId);
            }
        }
        return failures;
    }

    /// <summary>
    /// Splits an enrich-candidate list into <em>visible</em> (rows that
    /// pass the current <see cref="InboxFilters"/>) and <em>hidden</em>
    /// (everything else). Pulled out for unit testing — the partition
    /// must agree with the dashboard's "is this row currently shown?"
    /// answer or visible-first prioritization stops actually prioritizing
    /// what's on screen.
    /// </summary>
    internal static (List<PullRequestRow> Visible, List<PullRequestRow> Hidden) PartitionCandidates(
        IReadOnlyList<PullRequestRow> candidates,
        InboxFilters filters)
    {
        var visible = new List<PullRequestRow>();
        var hidden = new List<PullRequestRow>();
        foreach (var row in candidates)
        {
            if (filters.ShouldShow(row)) visible.Add(row);
            else hidden.Add(row);
        }
        return (visible, hidden);
    }

    /// <summary>
    /// Force an enrich pass on a single PR by URL — bypasses the
    /// <c>ListNeedingEnrichmentAsync</c> candidate filter so already-enriched
    /// rows can also be refreshed (e.g. to backfill new columns added by a
    /// schema bump). After the enrich call, the inbox row is re-pushed
    /// through <see cref="InboxState"/> so the UI updates in place.
    /// Returns true if a runtime owned the PR and the enrich completed.
    /// </summary>
    public async Task<(bool ok, string? error)> TriggerEnrichOneAsync(string prUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prUrl)) return (false, "Empty PR URL.");
        try
        {
            var config = await PrInboxConfig.LoadAsync(null);
            var (prRepo, threadRepo, snapRepo, syncRunRepo) = OpenFullRepos();
            var runtimes = new SourceFactory().Build(config);

            // Match runtime by SourceId via the row's recorded binding so we
            // never try to enrich a PR with the wrong identity / token.
            var row = await prRepo.GetAsync(prUrl, ct);
            if (row is null) return (false, "PR not found in cache.");

            var rt = runtimes.FirstOrDefault(r =>
                string.Equals(r.Source.SourceId, row.SourceId, StringComparison.Ordinal) &&
                string.Equals(r.Identity, row.IdentityUsed, StringComparison.Ordinal));
            if (rt is null) return (false, $"No configured source matches '{row.SourceId}' / '{row.IdentityUsed}'.");

            var orchestrator = new SyncOrchestrator(rt.Source, prRepo, snapRepo, threadRepo, syncRunRepo);
            var refreshed = await orchestrator.RunEnrichOneAsync(prUrl, ct);
            if (refreshed is null) return (false, "PR not owned by any source runtime.");

            // Push the updated row into the live UI so the threads cell and
            // any open /threads page see the new node ids immediately.
            try
            {
                var fresh = await prRepo.GetAsync(prUrl, ct);
                if (fresh is not null)
                {
                    var (open, bot, likelyDone) = await CountThreadsAsync(threadRepo, fresh.Identity, ct);
                    var snap = await snapRepo.GetLatestAsync(fresh.Identity, ct);
                    var drift = DriftInfo.Compute(fresh, snap);
                    _state.Upsert(InboxRow.FromRow(fresh, open, bot, drift, likelyDone));
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Post-enrich inbox refresh failed for {Url}", prUrl);
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Single-PR enrich for {Url} failed", prUrl);
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task<(int open, int bot, int likelyDone)> CountThreadsAsync(
        ObservedThreadRepository repo, PrIdentity id, CancellationToken ct)
    {
        try
        {
            var threads = await repo.GetOpenThreadsAsync(id, ct);
            var open = threads.Count;
            var bot = threads.Count(t => t.IsBot);

            // "Likely done" = number of distinct thread node ids whose
            // LATEST row matches DoneReplyHeuristic. Keep this in sync
            // with the per-row computation in Threads.razor.LoadAsync.
            var likelyDone = threads
                .Where(t => !string.IsNullOrEmpty(t.PlatformThreadNodeId))
                .GroupBy(t => t.PlatformThreadNodeId!, StringComparer.Ordinal)
                .Count(g => DoneReplyHeuristic.IsDoneReply(
                    g.OrderByDescending(r => r.FirstSeenAt).First().LastCommentBody));

            return (open, bot, likelyDone);
        }
        catch
        {
            return (0, 0, 0);
        }
    }

    private static (PullRequestRepository prRepo, ObservedThreadRepository threadRepo) OpenRepos()
    {
        var db = new PrInboxDb(PrInboxDb.DefaultUserConnectionString());
        return (new PullRequestRepository(db), new ObservedThreadRepository(db));
    }

    private static (PullRequestRepository prRepo, ObservedThreadRepository threadRepo,
                    PrSnapshotRepository snapRepo, SyncRunRepository syncRunRepo) OpenFullRepos()
    {
        var db = new PrInboxDb(PrInboxDb.DefaultUserConnectionString());
        new MigrationRunner().MigrateAsync(db.ConnectionString).GetAwaiter().GetResult();
        return (new PullRequestRepository(db),
                new ObservedThreadRepository(db),
                new PrSnapshotRepository(db),
                new SyncRunRepository(db));
    }
}
