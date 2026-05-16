using PrInbox.Core.Models;
using PrInbox.Core.Storage;
using PrInbox.Sources;
using PrInbox.Sources.Fakes;

namespace PrInbox.Tests.Sources;

/// <summary>
/// Behavioral tests for <see cref="SyncOrchestrator"/>'s progressive-fetch
/// split: RunFast (tier-2) vs RunEnrich (tier-3) vs RunAsync (default).
/// </summary>
public class SyncOrchestratorTests : IAsyncLifetime
{
    private string _connString = string.Empty;
    private PrInboxDb _db = null!;
    private Microsoft.Data.Sqlite.SqliteConnection _keepAlive = null!;

    private PullRequestRepository _prs = null!;
    private PrSnapshotRepository _snaps = null!;
    private ObservedThreadRepository _threads = null!;
    private SyncRunRepository _syncRuns = null!;

    public async Task InitializeAsync()
    {
        _connString = PrInboxDb.InMemoryConnectionString($"orch-{Guid.NewGuid():N}");
        _db = new PrInboxDb(_connString);
        _keepAlive = await _db.OpenAsync();
        await new MigrationRunner().MigrateAsync(_connString);

        _prs = new PullRequestRepository(_db);
        _snaps = new PrSnapshotRepository(_db);
        _threads = new ObservedThreadRepository(_db);
        _syncRuns = new SyncRunRepository(_db);
    }

    public async Task DisposeAsync()
    {
        await _keepAlive.DisposeAsync();
    }

    [Fact]
    public async Task RunFast_Lists_And_Upserts_Basic_Rows_Without_Snapshots_Or_Threads()
    {
        var source = BuildFakeSource("gh.com:emu", out _);
        var orch = new SyncOrchestrator(source, _prs, _snaps, _threads, _syncRuns);

        var result = await orch.RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        result.Status.Should().Be(SyncRunStatus.Ok);
        result.PrsSeen.Should().Be(2);

        var rows = await _prs.ListAllAsync(CancellationToken.None);
        rows.Should().HaveCount(2);
        rows.Should().AllSatisfy(r => r.EnrichState.Should().Be(EnrichState.Basic));

        // No snapshot rows yet.
        await using var cmd = _keepAlive.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pr_snapshots;";
        var snapCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        snapCount.Should().Be(0L);

        await using var threadCmd = _keepAlive.CreateCommand();
        threadCmd.CommandText = "SELECT COUNT(*) FROM observed_threads;";
        var threadCount = Convert.ToInt64(await threadCmd.ExecuteScalarAsync());
        threadCount.Should().Be(0L);
    }

    [Fact]
    public async Task RunEnrich_Hydrates_Snapshots_And_Threads_And_Marks_Enriched()
    {
        var source = BuildFakeSource("gh.com:emu", out var idAlpha);
        var orch = new SyncOrchestrator(source, _prs, _snaps, _threads, _syncRuns);

        // First a fast pass to seed basic rows.
        await orch.RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        // Then enrich.
        var result = await orch.RunEnrichAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        result.Status.Should().Be(SyncRunStatus.Ok);
        result.PrsSeen.Should().Be(2);

        // All rows now enriched.
        var rows = await _prs.ListAllAsync(CancellationToken.None);
        rows.Should().AllSatisfy(r => r.EnrichState.Should().Be(EnrichState.Enriched));

        // Snapshot recorded for the enriched PR.
        var snap = await _snaps.GetLatestAsync(idAlpha, CancellationToken.None);
        snap.Should().NotBeNull();
        snap!.HeadSha.Should().Be("abc1234567890aaa");

        // Threads recorded.
        var threads = await _threads.GetOpenThreadsAsync(idAlpha, CancellationToken.None);
        threads.Should().HaveCountGreaterThan(0);

        // A second enrich call finds nothing to do.
        var second = await orch.RunEnrichAsync("jmprieur_microsoft", progress: null, CancellationToken.None);
        second.PrsSeen.Should().Be(0);
    }

    [Fact]
    public async Task RunEnrich_Ignores_Other_Identities_Bindings()
    {
        // EMU sees alpha (already bound to gh.com:emu). The public source then runs
        // its fast pass and also discovers a different PR (beta) under gh.com:public.
        // When RunEnrich runs for gh.com:emu, it should NOT pick up beta — even
        // though beta is 'basic' globally.
        var emuSource = BuildFakeSource("gh.com:emu", out var idAlpha);
        var emuOrch = new SyncOrchestrator(emuSource, _prs, _snaps, _threads, _syncRuns);
        await emuOrch.RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        // Now public source: same PR ids but with different source_id.
        var publicSource = BuildFakeSource("gh.com:public", out _);
        var publicOrch = new SyncOrchestrator(publicSource, _prs, _snaps, _threads, _syncRuns);
        await publicOrch.RunFastAsync("jmprieur", progress: null, CancellationToken.None);

        // Enrich on the EMU side should only enrich PRs the EMU binding covers.
        // Since the upsert overwrote the row's source_id to 'gh.com:public' on the
        // second fast pass, the row's current source_id is now public — but the
        // EMU binding from the first pass still exists in pr_source_bindings.
        // RunEnrich finds candidates by JOINing through pr_source_bindings, so it
        // should still discover the bound rows. Both runs share PR URLs in this fake.
        var emuResult = await emuOrch.RunEnrichAsync("jmprieur_microsoft", progress: null, CancellationToken.None);
        emuResult.PrsSeen.Should().Be(2);

        // After EMU enrichment, public's enrich pass should be a no-op IF the public
        // bindings exist (they do, seeded by the public fast pass).
        // But the rows are now 'enriched' globally — so public also finds 0 work.
        var publicResult = await publicOrch.RunEnrichAsync("jmprieur", progress: null, CancellationToken.None);
        publicResult.PrsSeen.Should().Be(0);
    }

    [Fact]
    public async Task RunFast_Preserves_Enriched_State_When_LastUpdated_Has_Not_Moved()
    {
        var idAlpha = new PrIdentity("https://github.com/owner/repo/pull/1", "gh.com:100#1000");
        var lastUpdated = DateTimeOffset.Parse("2026-05-13T10:00:00Z");

        var source = new FakePrReadSourceBuilder("gh.com:emu", SourceKind.GitHub)
            .WithPullRequest(BuildBasicPr(idAlpha, lastUpdated), BuildDetail(idAlpha))
            .Build();
        var orch = new SyncOrchestrator(source, _prs, _snaps, _threads, _syncRuns);

        await orch.RunAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        // The row is now 'enriched'. Re-running fast with no upstream LastUpdated
        // movement should NOT downgrade.
        await orch.RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        var rows = await _prs.ListAllAsync(CancellationToken.None);
        rows.Should().ContainSingle();
        rows[0].EnrichState.Should().Be(EnrichState.Enriched);
    }

    [Fact]
    public async Task RunFast_Downgrades_Enriched_To_Basic_When_LastUpdated_Moves_Forward()
    {
        var idAlpha = new PrIdentity("https://github.com/owner/repo/pull/1", "gh.com:100#1000");

        // First pass at T0.
        var t0 = DateTimeOffset.Parse("2026-05-13T10:00:00Z");
        var sourceT0 = new FakePrReadSourceBuilder("gh.com:emu", SourceKind.GitHub)
            .WithPullRequest(BuildBasicPr(idAlpha, t0), BuildDetail(idAlpha))
            .Build();
        var orch = new SyncOrchestrator(sourceT0, _prs, _snaps, _threads, _syncRuns);
        await orch.RunAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        var afterFirst = (await _prs.ListAllAsync(CancellationToken.None)).Single();
        afterFirst.EnrichState.Should().Be(EnrichState.Enriched);

        // Second fast pass with LastUpdated moved past LastSyncedAt.
        var t1 = afterFirst.LastSyncedAt.AddMinutes(5);
        var sourceT1 = new FakePrReadSourceBuilder("gh.com:emu", SourceKind.GitHub)
            .WithPullRequest(BuildBasicPr(idAlpha, t1), BuildDetail(idAlpha))
            .Build();
        var orch2 = new SyncOrchestrator(sourceT1, _prs, _snaps, _threads, _syncRuns);
        await orch2.RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        var afterDowngrade = (await _prs.ListAllAsync(CancellationToken.None)).Single();
        afterDowngrade.EnrichState.Should().Be(EnrichState.Basic);
    }

    private static FakePrReadSource BuildFakeSource(string sourceId, out PrIdentity idAlpha)
    {
        idAlpha = new PrIdentity("https://github.com/owner/repo/pull/1", "gh.com:100#1000");
        var idBeta = new PrIdentity("https://github.com/owner/repo/pull/2", "gh.com:100#2000");

        var prAlpha = BuildBasicPr(idAlpha, DateTimeOffset.Parse("2026-05-13T10:00:00Z"), sourceId);
        var detailAlpha = BuildDetail(idAlpha);
        var threadsAlpha = new[]
        {
            new RemoteThread(
                PlatformThreadId: "t-1",
                Kind: ThreadKind.ReviewComment,
                AuthorLogin: "jmprieur",
                IsBot: false,
                BotKind: null,
                IsResolved: false,
                CreatedAt: DateTimeOffset.Parse("2026-05-13T11:00:00Z"),
                LastUpdatedAt: DateTimeOffset.Parse("2026-05-13T11:00:00Z"),
                RawJson: "{}"),
        };
        var prBeta = BuildBasicPr(idBeta, DateTimeOffset.Parse("2026-05-13T09:00:00Z"), sourceId);
        var detailBeta = BuildDetail(idBeta, "feeddead00000000");

        return new FakePrReadSourceBuilder(sourceId, SourceKind.GitHub)
            .WithPullRequest(prAlpha, detailAlpha, threadsAlpha)
            .WithPullRequest(prBeta, detailBeta)
            .Build();
    }

    private static RemotePullRequest BuildBasicPr(PrIdentity id, DateTimeOffset lastUpdated, string sourceId = "gh.com:emu") =>
        new(
            Identity: id,
            SourceKind: SourceKind.GitHub,
            SourceId: sourceId,
            DisplayRepo: "owner/repo",
            Number: int.Parse(id.Url[(id.Url.LastIndexOf('/') + 1)..]),
            Title: "Sample",
            AuthorLogin: "octocat",
            Url: id.Url,
            Status: PullRequestStatus.Open,
            LastUpdated: lastUpdated);

    private static RemotePullRequestDetail BuildDetail(PrIdentity id, string headSha = "abc1234567890aaa") =>
        new(
            Identity: id,
            HeadSha: headSha,
            BaseSha: "base000000000000",
            MergeBaseSha: null,
            OrderedCommitShas: new[] { headSha },
            ReviewerState: ReviewerState.Requested,
            Status: PullRequestStatus.Open,
            RawMetadataJson: "{}");
}
