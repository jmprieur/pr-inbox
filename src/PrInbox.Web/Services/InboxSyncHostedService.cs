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

    public InboxSyncHostedService(InboxState state, IConfiguration config, ILogger<InboxSyncHostedService> log)
    {
        _state = state;
        _config = config;
        _log = log;
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
            await TrySyncAsync(stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(intervalSec), stoppingToken); }
            catch (OperationCanceledException) { return; }

            await TrySyncAsync(stoppingToken);
        }
    }

    private async Task TrySyncAsync(CancellationToken ct)
    {
        try
        {
            await RunFastSyncAsync(ct);
            await RefreshFromCacheAsync(ct);
            await RunEnrichSyncAsync(ct);
            await RefreshFromCacheAsync(ct);
            _state.NoteSync($"Synced at {DateTimeOffset.UtcNow:HH:mm:ss} UTC");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sync iteration failed");
            _state.NoteSync($"Sync failed: {ex.Message}");
        }
    }

    private async Task RefreshFromCacheAsync(CancellationToken ct)
    {
        var (prRepo, threadRepo) = OpenRepos();
        var prs = await prRepo.ListAllAsync(ct);

        var rows = new List<InboxRow>(prs.Count);
        foreach (var pr in prs)
        {
            var (open, bot) = await CountThreadsAsync(threadRepo, pr.Identity, ct);
            rows.Add(InboxRow.FromRow(pr, open, bot));
        }
        _state.ReplaceAll(rows);
    }

    private async Task RunFastSyncAsync(CancellationToken ct)
    {
        var config = await PrInboxConfig.LoadAsync(null);
        if (config.Sources.Count == 0 && config.Ado.Projects.Count == 0) return;

        var (prRepo, threadRepo, snapRepo, syncRunRepo) = OpenFullRepos();
        var runtimes = new SourceFactory().Build(config);

        foreach (var rt in runtimes)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                var orchestrator = new SyncOrchestrator(rt.Source, prRepo, snapRepo, threadRepo, syncRunRepo);
                var progress = new Progress<SyncProgress>();
                await orchestrator.RunFastAsync(rt.Identity, progress, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Fast sync of {SourceId} failed", rt.Source.SourceId);
            }
        }
    }

    private async Task RunEnrichSyncAsync(CancellationToken ct)
    {
        var config = await PrInboxConfig.LoadAsync(null);
        if (config.Sources.Count == 0 && config.Ado.Projects.Count == 0) return;

        var (prRepo, threadRepo, snapRepo, syncRunRepo) = OpenFullRepos();
        var runtimes = new SourceFactory().Build(config);

        foreach (var rt in runtimes)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                var orchestrator = new SyncOrchestrator(rt.Source, prRepo, snapRepo, threadRepo, syncRunRepo);
                var progress = new Progress<SyncProgress>();
                await orchestrator.RunEnrichAsync(rt.Identity, progress, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Enrich of {SourceId} failed", rt.Source.SourceId);
            }
        }
    }

    private static async Task<(int open, int bot)> CountThreadsAsync(
        ObservedThreadRepository repo, PrIdentity id, CancellationToken ct)
    {
        try
        {
            var threads = await repo.GetOpenThreadsAsync(id, ct);
            var open = threads.Count;
            var bot = threads.Count(t => t.IsBot);
            return (open, bot);
        }
        catch
        {
            return (0, 0);
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
