using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PrInbox.Core.Models;
using PrInbox.Core.Storage;
using Xunit;

namespace PrInbox.Publishers.Tests;

/// <summary>
/// End-to-end test for <see cref="ThreadResolveOrchestrator"/> against an
/// in-memory SQLite-backed <see cref="ObservedThreadRepository"/> with a
/// recording publisher. Covers the rubber-duck-flagged corners: server-side
/// validation, dedupe across N rows of one thread, write-back includes
/// already-resolved bucket, failed ids do NOT get marked locally.
/// </summary>
public class ThreadResolveOrchestratorTests : IAsyncLifetime
{
    private string _connString = string.Empty;
    private PrInboxDb _db = null!;
    private Microsoft.Data.Sqlite.SqliteConnection _keepAlive = null!;
    private PullRequestRepository _prRepo = null!;
    private ObservedThreadRepository _threadRepo = null!;
    private const string Url = "https://github.com/agency-microsoft/playground/pull/5589";

    public async Task InitializeAsync()
    {
        _connString = PrInboxDb.InMemoryConnectionString($"thread-orch-{Guid.NewGuid():N}");
        _db = new PrInboxDb(_connString);
        _keepAlive = await _db.OpenAsync();
        await new MigrationRunner().MigrateAsync(_connString);

        _prRepo = new PullRequestRepository(_db);
        _threadRepo = new ObservedThreadRepository(_db);
        await _prRepo.UpsertAsync(SamplePrRow(), CancellationToken.None);
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    [Fact]
    public async Task Rejects_unknown_node_ids_without_calling_publisher()
    {
        await SeedOpenThreadAsync(commentId: 1, nodeId: "PRRT_real");

        var publisher = new RecordingThreadPublisher();
        var orch = new ThreadResolveOrchestrator(
            new FixedSelector(publisher), _prRepo, _threadRepo,
            NullLogger<ThreadResolveOrchestrator>.Instance);

        // All ids unknown → publisher never called, error returned.
        var result = await orch.ResolveAsync(
            Url,
            new[] { "PRRT_ghost1", "PRRT_ghost2" },
            dryRun: false,
            CancellationToken.None);

        publisher.LastRequest.Should().BeNull();
        result.UnknownNodeIds.Should().BeEquivalentTo(new[] { "PRRT_ghost1", "PRRT_ghost2" });
        result.LocalRowsMarked.Should().Be(0);
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Filters_unknown_ids_but_proceeds_with_known_ones()
    {
        await SeedOpenThreadAsync(commentId: 1, nodeId: "PRRT_real");

        var publisher = new RecordingThreadPublisher(forceStatus: ResolveBucket.Resolved);
        var orch = new ThreadResolveOrchestrator(
            new FixedSelector(publisher), _prRepo, _threadRepo,
            NullLogger<ThreadResolveOrchestrator>.Instance);

        var result = await orch.ResolveAsync(
            Url,
            new[] { "PRRT_real", "PRRT_ghost" },
            dryRun: false,
            CancellationToken.None);

        publisher.LastRequest!.ThreadNodeIds.Should().BeEquivalentTo(new[] { "PRRT_real" });
        result.UnknownNodeIds.Should().BeEquivalentTo(new[] { "PRRT_ghost" });
        result.LocalRowsMarked.Should().Be(1);
    }

    [Fact]
    public async Task DryRun_does_not_mark_local_rows()
    {
        await SeedOpenThreadAsync(commentId: 1, nodeId: "PRRT_one");

        var publisher = new RecordingThreadPublisher(forceStatus: ResolveBucket.Resolved);
        var orch = new ThreadResolveOrchestrator(
            new FixedSelector(publisher), _prRepo, _threadRepo,
            NullLogger<ThreadResolveOrchestrator>.Instance);

        var result = await orch.ResolveAsync(
            Url, new[] { "PRRT_one" }, dryRun: true, CancellationToken.None);

        result.DryRun.Should().BeTrue();
        result.LocalRowsMarked.Should().Be(0);
        publisher.LastRequest!.DryRun.Should().BeTrue();
        var stillOpen = await _threadRepo.GetOpenThreadsAsync(SampleIdentity(), CancellationToken.None);
        stillOpen.Should().ContainSingle("dry-run must not stamp resolved_at locally");
    }

    [Fact]
    public async Task Marks_one_thread_resolves_all_rows_sharing_its_node_id()
    {
        // GraphQL ReviewThread → 1 node id; REST emits N rows (root + replies).
        await SeedOpenThreadAsync(commentId: 100, nodeId: "PRRT_shared");
        await SeedOpenThreadAsync(commentId: 101, nodeId: "PRRT_shared");
        await SeedOpenThreadAsync(commentId: 200, nodeId: "PRRT_other");

        var publisher = new RecordingThreadPublisher(forceStatus: ResolveBucket.Resolved);
        var orch = new ThreadResolveOrchestrator(
            new FixedSelector(publisher), _prRepo, _threadRepo,
            NullLogger<ThreadResolveOrchestrator>.Instance);

        var result = await orch.ResolveAsync(
            Url, new[] { "PRRT_shared" }, dryRun: false, CancellationToken.None);

        result.LocalRowsMarked.Should().Be(2);
        var stillOpen = await _threadRepo.GetOpenThreadsAsync(SampleIdentity(), CancellationToken.None);
        stillOpen.Should().ContainSingle()
            .Which.PlatformThreadNodeId.Should().Be("PRRT_other");
    }

    [Fact]
    public async Task Marks_already_resolved_bucket_locally_too()
    {
        // If two reviewers race, GitHub returns "already resolved" — we
        // should still drop the local OpenThreadCount.
        await SeedOpenThreadAsync(commentId: 1, nodeId: "PRRT_one");

        var publisher = new RecordingThreadPublisher(forceStatus: ResolveBucket.AlreadyResolved);
        var orch = new ThreadResolveOrchestrator(
            new FixedSelector(publisher), _prRepo, _threadRepo,
            NullLogger<ThreadResolveOrchestrator>.Instance);

        var result = await orch.ResolveAsync(
            Url, new[] { "PRRT_one" }, dryRun: false, CancellationToken.None);

        result.LocalRowsMarked.Should().Be(1);
        result.PublisherResult!.AlreadyResolvedNodeIds.Should().ContainSingle();
    }

    [Fact]
    public async Task Does_not_mark_failed_ids_locally()
    {
        await SeedOpenThreadAsync(commentId: 1, nodeId: "PRRT_will_fail");

        var publisher = new RecordingThreadPublisher(forceStatus: ResolveBucket.Failed);
        var orch = new ThreadResolveOrchestrator(
            new FixedSelector(publisher), _prRepo, _threadRepo,
            NullLogger<ThreadResolveOrchestrator>.Instance);

        var result = await orch.ResolveAsync(
            Url, new[] { "PRRT_will_fail" }, dryRun: false, CancellationToken.None);

        result.LocalRowsMarked.Should().Be(0);
        var stillOpen = await _threadRepo.GetOpenThreadsAsync(SampleIdentity(), CancellationToken.None);
        stillOpen.Should().ContainSingle("failed resolve must not be reflected locally");
    }

    [Fact]
    public async Task Empty_selection_returns_failure_without_DB_or_publisher_calls()
    {
        var publisher = new RecordingThreadPublisher();
        var orch = new ThreadResolveOrchestrator(
            new FixedSelector(publisher), _prRepo, _threadRepo,
            NullLogger<ThreadResolveOrchestrator>.Instance);

        var result = await orch.ResolveAsync(
            Url, Array.Empty<string>(), dryRun: false, CancellationToken.None);

        publisher.LastRequest.Should().BeNull();
        result.Errors.Should().ContainMatch("*No threads selected*");
    }

    [Fact]
    public async Task Missing_PR_row_returns_friendly_failure()
    {
        var publisher = new RecordingThreadPublisher();
        var orch = new ThreadResolveOrchestrator(
            new FixedSelector(publisher), _prRepo, _threadRepo,
            NullLogger<ThreadResolveOrchestrator>.Instance);

        var result = await orch.ResolveAsync(
            "https://github.com/owner/missing/pull/9999",
            new[] { "PRRT_one" },
            dryRun: false,
            CancellationToken.None);

        publisher.LastRequest.Should().BeNull();
        result.Errors.Should().ContainMatch("*not found in inbox*");
    }

    // --------------------------------------------------------------------
    // Helpers
    // --------------------------------------------------------------------

    private static PrIdentity SampleIdentity() => new(Url, "gh.com:0#5589");

    private static PullRequestRow SamplePrRow() => new(
        Identity: SampleIdentity(),
        SourceKind: SourceKind.GitHub,
        SourceId: "gh.com",
        DisplayRepo: "agency-microsoft/playground",
        Number: 5589,
        Title: "thread resolve PR",
        AuthorLogin: "octocat",
        Url: Url,
        Status: PullRequestStatus.Open,
        TrackingReason: TrackingReason.Assigned,
        IdentityUsed: "jmprieur_microsoft",
        FirstSeenAt: DateTimeOffset.UtcNow.AddHours(-1),
        LastSyncedAt: DateTimeOffset.UtcNow,
        EnrichState: EnrichState.Enriched,
        LastBriefedHeadSha: null,
        LastReviewRunHeadSha: null,
        LastPostedReviewHeadSha: null);

    private async Task SeedOpenThreadAsync(long commentId, string nodeId)
    {
        var t = DateTimeOffset.UtcNow.AddMinutes(-30);
        await _threadRepo.UpsertManyAsync(
            SampleIdentity(),
            new[]
            {
                new RemoteThread(
                    PlatformThreadId: $"review-comment:{commentId}",
                    Kind: ThreadKind.ReviewComment,
                    AuthorLogin: "Copilot",
                    IsBot: true,
                    BotKind: BotKind.CopilotReview,
                    IsResolved: false,
                    CreatedAt: t,
                    LastUpdatedAt: t,
                    RawJson: "{}",
                    BodyExcerpt: $"comment {commentId}",
                    AnchorPath: "src/Foo.cs",
                    AnchorLine: 10,
                    PlatformThreadNodeId: nodeId),
            },
            syncedAt: t,
            CancellationToken.None);
    }

    private enum ResolveBucket { Resolved, AlreadyResolved, Failed }

    private sealed class RecordingThreadPublisher : IPrReviewPublisher
    {
        private readonly ResolveBucket _bucket;
        public ThreadResolveRequest? LastRequest { get; private set; }

        public RecordingThreadPublisher(ResolveBucket forceStatus = ResolveBucket.Resolved)
        {
            _bucket = forceStatus;
        }

        public string Kind => "fake-thread";

        public Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken ct)
            => Task.FromResult(PublishResult.Failure(Kind, "not used"));

        public Task<ThreadResolveResult> ResolveThreadsAsync(
            ThreadResolveRequest request, CancellationToken ct)
        {
            LastRequest = request;
            if (request.DryRun)
            {
                return Task.FromResult(ThreadResolveResult.DryRunPlan(
                    request.ThreadNodeIds, Kind, "dry-run"));
            }
            var resolved = _bucket == ResolveBucket.Resolved ? request.ThreadNodeIds : Array.Empty<string>();
            var already = _bucket == ResolveBucket.AlreadyResolved ? request.ThreadNodeIds : Array.Empty<string>();
            var failed = _bucket == ResolveBucket.Failed ? request.ThreadNodeIds : Array.Empty<string>();
            var errors = _bucket == ResolveBucket.Failed
                ? request.ThreadNodeIds.Select(id => $"{id}: simulated failure").ToArray()
                : Array.Empty<string>();
            return Task.FromResult(new ThreadResolveResult(
                Performed: true,
                ResolvedNodeIds: resolved,
                AlreadyResolvedNodeIds: already,
                FailedNodeIds: failed,
                IdentityUsed: Kind,
                Warnings: Array.Empty<string>(),
                Errors: errors));
        }
    }

    private sealed class FixedSelector : IPublisherSelector
    {
        private readonly IPrReviewPublisher _p;
        public FixedSelector(IPrReviewPublisher p) => _p = p;
        public IPrReviewPublisher Select(string prUrl) => _p;
        public IPrReviewPublisher SelectFor(string prUrl, string identityUsed) => _p;
        public string? IdentityForLogging(string prUrl) => "fake";
    }
}
