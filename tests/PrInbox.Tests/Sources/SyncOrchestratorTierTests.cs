using PrInbox.Core.Models;
using PrInbox.Core.Storage;
using PrInbox.Sources;
using PrInbox.Sources.Fakes;

namespace PrInbox.Tests.Sources;

/// <summary>
/// Tests for the visible-first enrichment plumbing added to
/// <see cref="SyncOrchestrator.RunEnrichAsync"/>:
/// the optional <c>precomputedCandidates</c> + <c>tierLabel</c> params.
/// These are the contract the
/// <c>InboxSyncHostedService.RunEnrichSyncAsync</c> two-pass loop depends on.
/// </summary>
public class SyncOrchestratorTierTests : IAsyncLifetime
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
        _connString = PrInboxDb.InMemoryConnectionString($"orch-tier-{Guid.NewGuid():N}");
        _db = new PrInboxDb(_connString);
        _keepAlive = await _db.OpenAsync();
        await new MigrationRunner().MigrateAsync(_connString);

        _prs = new PullRequestRepository(_db);
        _snaps = new PrSnapshotRepository(_db);
        _threads = new ObservedThreadRepository(_db);
        _syncRuns = new SyncRunRepository(_db);
    }

    public Task DisposeAsync() => _keepAlive.DisposeAsync().AsTask();

    // ---------- precomputedCandidates ----------

    [Fact]
    public async Task RunEnrich_With_Empty_Precomputed_List_Enriches_Nothing()
    {
        var source = BuildFakeSource(out _, out _);
        var orch = new SyncOrchestrator(source, _prs, _snaps, _threads, _syncRuns);

        // Seed two PRs as basic via fast pass.
        await orch.RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        // Passing an empty precomputed list means the orchestrator skips
        // the DB candidate query and processes zero rows.
        var result = await orch.RunEnrichAsync(
            "jmprieur_microsoft", progress: null, CancellationToken.None,
            precomputedCandidates: Array.Empty<PullRequestRow>(),
            tierLabel: "visible");

        result.Status.Should().Be(SyncRunStatus.Ok);
        result.PrsSeen.Should().Be(0);

        // Both rows remain Basic — nothing was enriched.
        var rows = await _prs.ListAllAsync(CancellationToken.None);
        rows.Should().HaveCount(2);
        rows.Should().AllSatisfy(r => r.EnrichState.Should().Be(EnrichState.Basic));
    }

    [Fact]
    public async Task RunEnrich_With_Precomputed_Subset_Enriches_Only_Listed_Rows()
    {
        var source = BuildFakeSource(out var idAlpha, out var idBeta);
        var orch = new SyncOrchestrator(source, _prs, _snaps, _threads, _syncRuns);
        await orch.RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        // Hand the orchestrator only alpha. Beta should remain Basic.
        var alphaRow = (await _prs.ListAllAsync(CancellationToken.None))
            .Single(r => r.Identity.Url == idAlpha.Url);

        var result = await orch.RunEnrichAsync(
            "jmprieur_microsoft", progress: null, CancellationToken.None,
            precomputedCandidates: new[] { alphaRow },
            tierLabel: "visible");

        result.PrsSeen.Should().Be(1);

        var alphaAfter = await _prs.GetAsync(idAlpha.Url, CancellationToken.None);
        var betaAfter  = await _prs.GetAsync(idBeta.Url, CancellationToken.None);

        alphaAfter!.EnrichState.Should().Be(EnrichState.Enriched);
        betaAfter!.EnrichState.Should().Be(EnrichState.Basic);
    }

    [Fact]
    public async Task RunEnrich_With_Null_Precomputed_Uses_DB_Candidate_List()
    {
        // Belt-and-suspenders: the legacy default-args call site must still
        // pull from ListNeedingEnrichmentAsync. This protects every existing
        // caller (CLI, internal RunAsync, the older test suite).
        var source = BuildFakeSource(out _, out _);
        var orch = new SyncOrchestrator(source, _prs, _snaps, _threads, _syncRuns);
        await orch.RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        var result = await orch.RunEnrichAsync(
            "jmprieur_microsoft", progress: null, CancellationToken.None);
        // precomputedCandidates = null (default)

        result.PrsSeen.Should().Be(2);

        var rows = await _prs.ListAllAsync(CancellationToken.None);
        rows.Should().AllSatisfy(r => r.EnrichState.Should().Be(EnrichState.Enriched));
    }

    // ---------- tierLabel ----------

    [Fact]
    public async Task RunEnrich_With_TierLabel_Surfaces_In_Progress_Header()
    {
        var source = BuildFakeSource(out _, out _);
        var orch = new SyncOrchestrator(source, _prs, _snaps, _threads, _syncRuns);
        await orch.RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        var messages = new List<string>();
        var progress = new SyncProgressRecorder(p => messages.Add(p.Message));

        await orch.RunEnrichAsync(
            "jmprieur_microsoft", progress, CancellationToken.None,
            tierLabel: "visible");

        messages.Should().NotBeEmpty();
        // First message is the "Enriching..." header — it must include the tier.
        messages.First().Should().StartWith("Enriching (visible):");
    }

    [Fact]
    public async Task RunEnrich_Without_TierLabel_Uses_Plain_Header()
    {
        // Backwards compatibility: existing callers that don't pass a label
        // must see exactly the original "Enriching:" prefix — no parens.
        var source = BuildFakeSource(out _, out _);
        var orch = new SyncOrchestrator(source, _prs, _snaps, _threads, _syncRuns);
        await orch.RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        var messages = new List<string>();
        var progress = new SyncProgressRecorder(p => messages.Add(p.Message));

        await orch.RunEnrichAsync("jmprieur_microsoft", progress, CancellationToken.None);

        // "Enriching: " (with space) — not "Enriching (anything):"
        messages.First().Should().StartWith("Enriching: ");
    }

    [Fact]
    public async Task RunEnrich_With_Empty_TierLabel_Uses_Plain_Header()
    {
        // Edge case: an empty (but non-null) tierLabel from a caller that
        // forgot to set it shouldn't produce "Enriching ():" — fall back to
        // the bare prefix.
        var source = BuildFakeSource(out _, out _);
        var orch = new SyncOrchestrator(source, _prs, _snaps, _threads, _syncRuns);
        await orch.RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        var messages = new List<string>();
        var progress = new SyncProgressRecorder(p => messages.Add(p.Message));

        await orch.RunEnrichAsync(
            "jmprieur_microsoft", progress, CancellationToken.None,
            tierLabel: "");

        messages.First().Should().StartWith("Enriching: ");
    }

    // ---------- Cancellation ----------

    [Fact]
    public async Task RunEnrich_Cancellation_Before_Loop_Throws_OperationCanceled()
    {
        var source = BuildFakeSource(out _, out _);
        var orch = new SyncOrchestrator(source, _prs, _snaps, _threads, _syncRuns);
        await orch.RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // With cancellation set before the call, the orchestrator should
        // propagate OperationCanceledException rather than silently complete.
        var act = async () => await orch.RunEnrichAsync(
            "jmprieur_microsoft", progress: null, cts.Token,
            precomputedCandidates: (await _prs.ListAllAsync(CancellationToken.None)).ToArray(),
            tierLabel: "visible");

        await act.Should().ThrowAsync<OperationCanceledException>();

        // Critical for the host's pass-1/pass-2 invariant: a cancelled visible
        // pass must surface up so the host's outer ct check skips pass 2.
        var rows = await _prs.ListAllAsync(CancellationToken.None);
        rows.Should().AllSatisfy(r => r.EnrichState.Should().Be(EnrichState.Basic));
    }

    // ---------- fixture ----------

    /// <summary>
    /// Captures every <see cref="SyncProgress"/> reported during a run so
    /// the message stream can be asserted on. <see cref="Progress{T}"/>
    /// would also work but its callback is posted async — we want a
    /// synchronous, deterministic capture here.
    /// </summary>
    private sealed class SyncProgressRecorder : IProgress<SyncProgress>
    {
        private readonly Action<SyncProgress> _onReport;
        public SyncProgressRecorder(Action<SyncProgress> onReport) => _onReport = onReport;
        public void Report(SyncProgress value) => _onReport(value);
    }

    private static FakePrReadSource BuildFakeSource(out PrIdentity idAlpha, out PrIdentity idBeta)
    {
        idAlpha = new PrIdentity("https://github.com/owner/repo/pull/1", "gh.com:100#1000");
        idBeta  = new PrIdentity("https://github.com/owner/repo/pull/2", "gh.com:100#2000");

        var prAlpha = BuildBasicPr(idAlpha, DateTimeOffset.Parse("2026-05-13T10:00:00Z"));
        var prBeta  = BuildBasicPr(idBeta,  DateTimeOffset.Parse("2026-05-13T09:00:00Z"));

        return new FakePrReadSourceBuilder("gh.com:emu", SourceKind.GitHub)
            .WithPullRequest(prAlpha, BuildDetail(idAlpha))
            .WithPullRequest(prBeta,  BuildDetail(idBeta, "feeddead00000000"))
            .Build();
    }

    private static RemotePullRequest BuildBasicPr(PrIdentity id, DateTimeOffset lastUpdated) =>
        new(
            Identity: id,
            SourceKind: SourceKind.GitHub,
            SourceId: "gh.com:emu",
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
