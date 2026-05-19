using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PrInbox.Core.Credentials;
using PrInbox.Core.Models;
using PrInbox.Core.Storage;
using PrInbox.Sources;
using PrInbox.Web.Services;

namespace PrInbox.Tests.Web;

/// <summary>
/// Locks the contract that <see cref="InboxSyncHostedService"/>'s tier-2
/// fast pass fans out across runtimes in parallel — each source spends
/// most of its time on paginated REST calls, so serial execution costs
/// real wall-clock seconds when N &gt; 1. The barrier-coordinated test
/// proves both sources are inside <c>ListAssignedFastAsync</c> at the
/// same instant; the per-source tests guard the failure-isolation
/// semantics that the parallel fan-out depends on (one bad source must
/// not poison the others).
/// </summary>
public class InboxSyncHostedServiceParallelFastTests : IAsyncLifetime
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
        _connString = PrInboxDb.InMemoryConnectionString($"par-{Guid.NewGuid():N}");
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

    // ---------- RunOneFastAsync ----------

    [Fact]
    public async Task RunOneFastAsync_With_FakeSource_Persists_And_Returns_Zero()
    {
        var source = BuildHappySource("gh.com:emu", prCount: 2);
        var rt = new RuntimeSource(source, new StubTokenProvider(source.SourceId), "jmprieur_microsoft");

        var result = await InboxSyncHostedService.RunOneFastAsync(
            rt, _prs, _snaps, _threads, _syncRuns, NullLogger.Instance, CancellationToken.None);

        result.Should().Be(0);
        var rows = await _prs.ListAllAsync(CancellationToken.None);
        rows.Should().HaveCount(2);
        rows.Should().AllSatisfy(r => r.EnrichState.Should().Be(EnrichState.Basic));
    }

    [Fact]
    public async Task RunOneFastAsync_When_Source_Throws_Returns_One()
    {
        var source = new ThrowingFakeSource(
            "gh.com:public",
            new InvalidOperationException("simulated upstream failure"));
        var rt = new RuntimeSource(source, new StubTokenProvider(source.SourceId), "jmprieur");

        var result = await InboxSyncHostedService.RunOneFastAsync(
            rt, _prs, _snaps, _threads, _syncRuns, NullLogger.Instance, CancellationToken.None);

        // Throwing source -> fast pass status Failed -> tally += 1, but the
        // call itself must NOT propagate. Failure isolation is the whole
        // point of returning an int instead of letting exceptions escape.
        result.Should().Be(1);
        var rows = await _prs.ListAllAsync(CancellationToken.None);
        rows.Should().BeEmpty();
    }

    // ---------- RunFastSyncAsync (instance, parallel) ----------

    [Fact]
    public async Task RunFastSync_Across_Multiple_Sources_Runs_In_Parallel()
    {
        // Two barrier-coordinated fakes. Each fires `Entered` when its
        // ListAssignedFastAsync is invoked, then blocks on `Release`
        // before yielding. The test sets Release ONLY after both
        // sources have signaled Entered — which can only happen if the
        // host called them concurrently. A sequential loop would
        // deadlock here (source A waits on Release; the test waits for
        // source B's Entered; source B never starts because A holds
        // the loop).
        var alpha = new BarrierFakeSource("gh.com:emu");
        var beta = new BarrierFakeSource("gh.com:public");
        var runtimes = new[]
        {
            new RuntimeSource(alpha, new StubTokenProvider(alpha.SourceId), "jmprieur_microsoft"),
            new RuntimeSource(beta, new StubTokenProvider(beta.SourceId), "jmprieur"),
        };

        var host = NewHost();
        var hostTask = host.RunFastSyncAsync(runtimes, _prs, _snaps, _threads, _syncRuns, CancellationToken.None);

        // Wait for both sources to enter listing — if this completes,
        // the host has run them in parallel. Bounded by 5s so a future
        // regression to sequential won't hang the suite forever.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(alpha.Entered.Task, beta.Entered.Task).WaitAsync(cts.Token);

        // Now release both and let them complete.
        alpha.Release.TrySetResult();
        beta.Release.TrySetResult();

        var failures = await hostTask;
        failures.Should().Be(0);
    }

    [Fact]
    public async Task RunFastSync_With_Empty_Runtimes_Returns_Zero()
    {
        var host = NewHost();
        var failures = await host.RunFastSyncAsync(
            Array.Empty<RuntimeSource>(), _prs, _snaps, _threads, _syncRuns, CancellationToken.None);
        failures.Should().Be(0);
    }

    [Fact]
    public async Task RunFastSync_Sums_Per_Source_Failures()
    {
        // One healthy source + one throwing source. Healthy returns 0,
        // throwing returns 1, total should be exactly 1. The healthy
        // source must still persist its rows (isolation).
        var healthy = BuildHappySource("gh.com:emu", prCount: 1);
        var bad = new ThrowingFakeSource(
            "gh.com:public",
            new InvalidOperationException("simulated"));

        var runtimes = new[]
        {
            new RuntimeSource(healthy, new StubTokenProvider(healthy.SourceId), "jmprieur_microsoft"),
            new RuntimeSource(bad, new StubTokenProvider(bad.SourceId), "jmprieur"),
        };

        var host = NewHost();
        var failures = await host.RunFastSyncAsync(
            runtimes, _prs, _snaps, _threads, _syncRuns, CancellationToken.None);

        failures.Should().Be(1);
        var rows = await _prs.ListAllAsync(CancellationToken.None);
        rows.Should().HaveCount(1);
    }

    // ---------- fixture helpers ----------

    private static InboxSyncHostedService NewHost()
    {
        var config = new ConfigurationBuilder().Build();
        return new InboxSyncHostedService(
            new InboxState(),
            config,
            NullLogger<InboxSyncHostedService>.Instance);
    }

    private static PrInbox.Sources.Fakes.FakePrReadSource BuildHappySource(string sourceId, int prCount)
    {
        var builder = new PrInbox.Sources.Fakes.FakePrReadSourceBuilder(sourceId, SourceKind.GitHub);
        for (var i = 1; i <= prCount; i++)
        {
            var id = new PrIdentity(
                $"https://github.com/owner/{sourceId.Replace(':', '-')}/pull/{i}",
                $"{sourceId}:{i}#{i}000");
            var pr = new RemotePullRequest(
                Identity: id,
                SourceKind: SourceKind.GitHub,
                SourceId: sourceId,
                DisplayRepo: $"owner/{sourceId.Replace(':', '-')}",
                Number: i,
                Title: "Sample",
                AuthorLogin: "octocat",
                Url: id.Url,
                Status: PullRequestStatus.Open,
                LastUpdated: DateTimeOffset.Parse("2026-05-18T10:00:00Z"));
            var detail = new RemotePullRequestDetail(
                Identity: id,
                HeadSha: "abc1234567890aaa",
                BaseSha: "base000000000000",
                MergeBaseSha: null,
                OrderedCommitShas: new[] { "abc1234567890aaa" },
                ReviewerState: ReviewerState.Requested,
                Status: PullRequestStatus.Open,
                RawMetadataJson: "{}");
            builder = builder.WithPullRequest(pr, detail);
        }
        return builder.Build();
    }

    private sealed class StubTokenProvider : ITokenProvider
    {
        public StubTokenProvider(string sourceId) { SourceId = sourceId; }
        public string SourceId { get; }
        public Task<string> GetTokenAsync(CancellationToken ct = default) => Task.FromResult("stub-token");
        public Task<string?> GetAuthenticatedIdentityAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    /// <summary>
    /// Fake source that throws synchronously the moment listing is
    /// attempted. Used to prove failure isolation: one bad source must
    /// not poison the parallel fan-out.
    /// </summary>
    private sealed class ThrowingFakeSource : IPrReadSource
    {
        private readonly Exception _ex;
        public ThrowingFakeSource(string sourceId, Exception ex)
        {
            SourceId = sourceId;
            _ex = ex;
        }
        public string SourceId { get; }
        public SourceKind Kind => SourceKind.GitHub;
        public SourceCapabilities Capabilities => new(
            SupportsGlobalReviewerInbox: true,
            SupportsThreadResolution: true,
            SupportsBotAuthorClassification: true,
            SupportsReviewRequestTimestamps: true,
            SupportsStableRepoIds: true,
            SupportsForcePushDetection: true);
        public IAsyncEnumerable<RemotePullRequest> ListAssignedFastAsync(CancellationToken ct) =>
            ThrowAsync(_ex, ct);
        private static async IAsyncEnumerable<RemotePullRequest> ThrowAsync(
            Exception ex,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            throw ex;
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
        public Task<PrEnrichmentBundle> EnrichAsync(PrIdentity id, CancellationToken ct) => throw _ex;
        public Task<IReadOnlyList<RemoteCommit>> GetCommitsAsync(PrIdentity id, CancellationToken ct) => throw _ex;
        public Task<CompareResult> CompareAsync(PrIdentity id, string previousHeadSha, string currentHeadSha, CancellationToken ct) => throw _ex;
    }

    /// <summary>
    /// Barrier-coordinated fake. Fires <see cref="Entered"/> the moment
    /// <see cref="ListAssignedFastAsync"/> is called, then awaits
    /// <see cref="Release"/> before yielding any PR. Lets a test prove
    /// two sources are inside listing simultaneously — which can only
    /// happen if the host parallelizes them.
    /// </summary>
    private sealed class BarrierFakeSource : IPrReadSource
    {
        public BarrierFakeSource(string sourceId) { SourceId = sourceId; }
        public string SourceId { get; }
        public SourceKind Kind => SourceKind.GitHub;
        public SourceCapabilities Capabilities => new(
            SupportsGlobalReviewerInbox: true,
            SupportsThreadResolution: true,
            SupportsBotAuthorClassification: true,
            SupportsReviewRequestTimestamps: true,
            SupportsStableRepoIds: true,
            SupportsForcePushDetection: true);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<RemotePullRequest> ListAssignedFastAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            // Yield a single PR so the orchestrator records a sync_run
            // success — keeps the test asserting on the no-failures path.
            var id = new PrIdentity(
                $"https://github.com/owner/{SourceId.Replace(':', '-')}/pull/1",
                $"{SourceId}:1#1000");
            yield return new RemotePullRequest(
                Identity: id,
                SourceKind: SourceKind.GitHub,
                SourceId: SourceId,
                DisplayRepo: $"owner/{SourceId.Replace(':', '-')}",
                Number: 1,
                Title: "Sample",
                AuthorLogin: "octocat",
                Url: id.Url,
                Status: PullRequestStatus.Open,
                LastUpdated: DateTimeOffset.Parse("2026-05-18T10:00:00Z"));
        }

        public Task<PrEnrichmentBundle> EnrichAsync(PrIdentity id, CancellationToken ct) =>
            throw new NotSupportedException("Fast pass only.");
        public Task<IReadOnlyList<RemoteCommit>> GetCommitsAsync(PrIdentity id, CancellationToken ct) =>
            throw new NotSupportedException("Fast pass only.");
        public Task<CompareResult> CompareAsync(PrIdentity id, string previousHeadSha, string currentHeadSha, CancellationToken ct) =>
            throw new NotSupportedException("Fast pass only.");
    }
}
