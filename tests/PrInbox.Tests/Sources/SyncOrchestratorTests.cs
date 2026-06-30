using System.Collections.Concurrent;
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

    [Fact]
    public async Task RunFast_Result_Includes_SeenUrls()
    {
        var source = BuildFakeSource("gh.com:emu", out var idAlpha);
        var orch = new SyncOrchestrator(source, _prs, _snaps, _threads, _syncRuns);

        var result = await orch.RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        result.SeenUrls.Should().NotBeNull();
        result.SeenUrls!.Should().Contain(idAlpha.Url);
        result.SeenUrls.Should().HaveCount(2);
    }

    [Fact]
    public async Task RunEnrich_Propagates_Detail_Status_To_PullRequests_Status()
    {
        // Initial fast pass marks the PR as open. The detail then reports
        // "merged" — RunEnrich should propagate the new status.
        var idAlpha = new PrIdentity("https://github.com/owner/repo/pull/1", "gh.com:100#1000");
        var prOpen = BuildBasicPr(idAlpha, DateTimeOffset.Parse("2026-05-13T10:00:00Z"));
        var detailMerged = BuildDetail(idAlpha) with { Status = PullRequestStatus.Merged };

        var source = new FakePrReadSourceBuilder("gh.com:emu", SourceKind.GitHub)
            .WithPullRequest(prOpen, detailMerged)
            .Build();
        var orch = new SyncOrchestrator(source, _prs, _snaps, _threads, _syncRuns);

        await orch.RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);
        await orch.RunEnrichAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        var row = await _prs.GetAsync(idAlpha.Url, CancellationToken.None);
        row!.Status.Should().Be(PullRequestStatus.Merged);
    }

    [Fact]
    public async Task RunDisappearedSweep_Stamps_DisappearedAt_When_Still_Open()
    {
        // Seed: one PR in the DB as open, bound to (source, identity).
        // The sweep is told that PR did NOT appear in the latest fast pass.
        // Enrich still reports it as open -> stamp disappeared_at.
        var idAlpha = new PrIdentity("https://github.com/owner/repo/pull/1", "gh.com:100#1000");
        var source = new FakePrReadSourceBuilder("gh.com:emu", SourceKind.GitHub)
            .WithPullRequest(
                BuildBasicPr(idAlpha, DateTimeOffset.Parse("2026-05-13T10:00:00Z")),
                BuildDetail(idAlpha))
            .Build();
        var orch = new SyncOrchestrator(source, _prs, _snaps, _threads, _syncRuns);
        await orch.RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        await orch.RunDisappearedSweepAsync(
            "jmprieur_microsoft",
            seenUrls: new HashSet<string>(),
            cap: 5,
            CancellationToken.None);

        var row = await _prs.GetAsync(idAlpha.Url, CancellationToken.None);
        row!.Status.Should().Be(PullRequestStatus.Open);
        row.DisappearedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RunDisappearedSweep_Updates_Status_When_Now_Merged_And_Clears_Disappeared()
    {
        // Seed: PR is open in DB. Enrich now reports merged. After the
        // sweep, status should be 'merged' and disappeared_at should NOT
        // be set (the row isn't "still open" any more).
        var idAlpha = new PrIdentity("https://github.com/owner/repo/pull/2", "gh.com:100#2000");
        var source = new FakePrReadSourceBuilder("gh.com:emu", SourceKind.GitHub)
            .WithPullRequest(
                BuildBasicPr(idAlpha, DateTimeOffset.Parse("2026-05-13T10:00:00Z")),
                BuildDetail(idAlpha) with { Status = PullRequestStatus.Merged })
            .Build();
        var orch = new SyncOrchestrator(source, _prs, _snaps, _threads, _syncRuns);
        await orch.RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        await orch.RunDisappearedSweepAsync(
            "jmprieur_microsoft",
            seenUrls: new HashSet<string>(),
            cap: 5,
            CancellationToken.None);

        var row = await _prs.GetAsync(idAlpha.Url, CancellationToken.None);
        row!.Status.Should().Be(PullRequestStatus.Merged);
        row.DisappearedAt.Should().BeNull();
    }

    [Fact]
    public async Task RunDisappearedSweep_Skips_Urls_That_Were_Seen()
    {
        // Seed two PRs. Tell the sweep one of them was seen. Only the
        // other should be touched.
        var idAlpha = new PrIdentity("https://github.com/owner/repo/pull/3", "gh.com:100#3000");
        var idBeta  = new PrIdentity("https://github.com/owner/repo/pull/4", "gh.com:100#4000");

        var source = new FakePrReadSourceBuilder("gh.com:emu", SourceKind.GitHub)
            .WithPullRequest(BuildBasicPr(idAlpha, DateTimeOffset.Parse("2026-05-13T10:00:00Z")),
                             BuildDetail(idAlpha))
            .WithPullRequest(BuildBasicPr(idBeta, DateTimeOffset.Parse("2026-05-13T10:00:00Z")),
                             BuildDetail(idBeta) with { Status = PullRequestStatus.Merged })
            .Build();
        var orch = new SyncOrchestrator(source, _prs, _snaps, _threads, _syncRuns);
        await orch.RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        await orch.RunDisappearedSweepAsync(
            "jmprieur_microsoft",
            seenUrls: new HashSet<string> { idAlpha.Url },
            cap: 5,
            CancellationToken.None);

        // alpha untouched (still open, no disappeared stamp).
        var alpha = await _prs.GetAsync(idAlpha.Url, CancellationToken.None);
        alpha!.Status.Should().Be(PullRequestStatus.Open);
        alpha.DisappearedAt.Should().BeNull();

        // beta got re-enriched and learned it's merged.
        var beta = await _prs.GetAsync(idBeta.Url, CancellationToken.None);
        beta!.Status.Should().Be(PullRequestStatus.Merged);
    }

    [Fact]
    public async Task RunTtlSweep_Stamps_LastSweptAt_On_Oldest_Open_Rows()
    {
        var idAlpha = new PrIdentity("https://github.com/owner/repo/pull/5", "gh.com:100#5000");
        var idBeta  = new PrIdentity("https://github.com/owner/repo/pull/6", "gh.com:100#6000");

        var source = new FakePrReadSourceBuilder("gh.com:emu", SourceKind.GitHub)
            .WithPullRequest(BuildBasicPr(idAlpha, DateTimeOffset.Parse("2026-05-13T10:00:00Z")),
                             BuildDetail(idAlpha))
            .WithPullRequest(BuildBasicPr(idBeta, DateTimeOffset.Parse("2026-05-13T11:00:00Z")),
                             BuildDetail(idBeta))
            .Build();
        var orch = new SyncOrchestrator(source, _prs, _snaps, _threads, _syncRuns);
        await orch.RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        await orch.RunTtlSweepAsync("jmprieur_microsoft", n: 2, CancellationToken.None);

        var alpha = await _prs.GetAsync(idAlpha.Url, CancellationToken.None);
        var beta  = await _prs.GetAsync(idBeta.Url, CancellationToken.None);
        alpha!.LastSweptAt.Should().NotBeNull();
        beta!.LastSweptAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RunFast_Persists_LastUpstreamUpdatedAt_From_Source()
    {
        // Asserts the bridge from RemotePullRequest.LastUpdated → DB
        // pull_requests.last_upstream_updated_at via SyncOrchestrator's
        // UpsertFastAsync path. This is the source-of-truth for the
        // "Recent" sort in the inbox popovers.
        var idAlpha = new PrIdentity("https://github.com/owner/repo/pull/1", "gh.com:100#1000");
        var lastUpdated = DateTimeOffset.Parse("2026-05-18T15:30:00Z");

        var source = new FakePrReadSourceBuilder("gh.com:emu", SourceKind.GitHub)
            .WithPullRequest(BuildBasicPr(idAlpha, lastUpdated), BuildDetail(idAlpha))
            .Build();
        var orch = new SyncOrchestrator(source, _prs, _snaps, _threads, _syncRuns);

        await orch.RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        var row = await _prs.GetAsync(idAlpha.Url, CancellationToken.None);
        row.Should().NotBeNull();
        row!.LastUpstreamUpdatedAt.Should().Be(lastUpdated);
    }

    [Fact]
    public async Task RunFast_Persists_UpstreamCreatedAt_From_Source()
    {
        // Asserts the bridge from RemotePullRequest.CreatedAt → DB
        // pull_requests.upstream_created_at via SyncOrchestrator's
        // UpsertFastAsync path. This is the source-of-truth for the inbox
        // "Age" column.
        var idAlpha = new PrIdentity("https://github.com/owner/repo/pull/1", "gh.com:100#1000");
        var opened = DateTimeOffset.Parse("2026-04-02T09:15:00Z");
        var pr = BuildBasicPr(idAlpha, DateTimeOffset.Parse("2026-05-18T15:30:00Z"))
            with { CreatedAt = opened };

        var source = new FakePrReadSourceBuilder("gh.com:emu", SourceKind.GitHub)
            .WithPullRequest(pr, BuildDetail(idAlpha))
            .Build();
        var orch = new SyncOrchestrator(source, _prs, _snaps, _threads, _syncRuns);

        await orch.RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        var row = await _prs.GetAsync(idAlpha.Url, CancellationToken.None);
        row.Should().NotBeNull();
        row!.UpstreamCreatedAt.Should().Be(opened);
    }

    // ----- Visible-first contract -----
    //
    // InboxSyncHostedService relies on RunEnrichAsync accepting a
    // pre-computed candidate subset (so it can enrich VISIBLE rows
    // before HIDDEN rows) and on the tier label flowing into the
    // progress stream so the UI / logs can tell the two passes apart.
    // These two tests lock that contract — if either signature drifts,
    // visible-first prioritization quietly breaks.

    [Fact]
    public async Task RunEnrich_With_PrecomputedCandidates_Only_Enriches_That_Subset()
    {
        var source = BuildFakeSource("gh.com:emu", out var idAlpha);
        var orch = new SyncOrchestrator(source, _prs, _snaps, _threads, _syncRuns);

        // Fast pass to seed alpha + beta as basic rows.
        await orch.RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        // Caller hands us ONLY alpha — beta must stay Basic.
        var alphaRow = await _prs.GetAsync(idAlpha.Url, CancellationToken.None);
        alphaRow.Should().NotBeNull();

        var result = await orch.RunEnrichAsync(
            "jmprieur_microsoft",
            progress: null,
            CancellationToken.None,
            precomputedCandidates: new[] { alphaRow! });

        result.Status.Should().Be(SyncRunStatus.Ok);
        result.PrsSeen.Should().Be(1);

        var rows = await _prs.ListAllAsync(CancellationToken.None);
        var enriched = rows.Where(r => r.EnrichState == EnrichState.Enriched).ToList();
        var basic = rows.Where(r => r.EnrichState == EnrichState.Basic).ToList();

        enriched.Should().ContainSingle(r => r.Identity.Url == idAlpha.Url);
        basic.Should().ContainSingle(r => r.Identity.Url != idAlpha.Url);
    }

    [Fact]
    public async Task RunEnrich_With_TierLabel_Annotates_Progress()
    {
        var source = BuildFakeSource("gh.com:emu", out var idAlpha);
        var orch = new SyncOrchestrator(source, _prs, _snaps, _threads, _syncRuns);

        await orch.RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        var alphaRow = await _prs.GetAsync(idAlpha.Url, CancellationToken.None);
        // Progress<T> callbacks can run concurrently on thread-pool threads
        // (IProgress<T> makes no thread-affinity guarantee), so the sink must
        // be thread-safe — a plain List<T>.Add races two reports and can drop one.
        var reports = new ConcurrentQueue<SyncProgress>();
        var progress = new Progress<SyncProgress>(reports.Enqueue);

        await orch.RunEnrichAsync(
            "jmprieur_microsoft",
            progress,
            CancellationToken.None,
            precomputedCandidates: new[] { alphaRow! },
            tierLabel: "visible");

        // Wait for the exact report we assert on; Progress<T> posts asynchronously.
        for (var i = 0; i < 40 && !reports.Any(r => r.Message.Contains("Enriching (visible)")); i++)
        {
            await Task.Delay(25);
        }

        reports.Should().NotBeEmpty();
        reports.Should().Contain(r => r.Message.Contains("Enriching (visible)"));
    }

    [Fact]
    public async Task ReviewerReappearance_Reactivates_PreviouslyAssigned_To_Assigned()
    {
        var id = new PrIdentity("https://github.com/owner/repo/pull/1", "gh.com:100#1000");
        var when = DateTimeOffset.Parse("2026-05-13T10:00:00Z");

        // T0: PR present in the reviewer query → assigned.
        var t0 = new FakePrReadSourceBuilder("gh.com:emu", SourceKind.GitHub)
            .WithPullRequest(BuildBasicPr(id, when), BuildDetail(id))
            .Build();
        await new SyncOrchestrator(t0, _prs, _snaps, _threads, _syncRuns)
            .RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);
        (await _prs.GetAsync(id.Url, CancellationToken.None))!.TrackingReason
            .Should().Be(TrackingReason.Assigned);

        // T1: PR gone (e.g. user submitted a review) → previously_assigned.
        var t1 = new FakePrReadSourceBuilder("gh.com:emu", SourceKind.GitHub).Build();
        await new SyncOrchestrator(t1, _prs, _snaps, _threads, _syncRuns)
            .RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);
        (await _prs.GetAsync(id.Url, CancellationToken.None))!.TrackingReason
            .Should().Be(TrackingReason.PreviouslyAssigned);

        // T2: re-requested → reappears in the reviewer query → reactivated.
        var t2 = new FakePrReadSourceBuilder("gh.com:emu", SourceKind.GitHub)
            .WithPullRequest(BuildBasicPr(id, when), BuildDetail(id))
            .Build();
        await new SyncOrchestrator(t2, _prs, _snaps, _threads, _syncRuns)
            .RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);
        (await _prs.GetAsync(id.Url, CancellationToken.None))!.TrackingReason
            .Should().Be(TrackingReason.Assigned);
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
