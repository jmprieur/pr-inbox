using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PrInbox.Core.Findings;
using PrInbox.Core.Models;
using PrInbox.Core.Storage;
using Xunit;

namespace PrInbox.Publishers.Tests;

/// <summary>
/// End-to-end test for <see cref="ReviewPublishOrchestrator"/> + an in-memory
/// SQLite-backed <see cref="PostedReviewRepository"/>. Verifies dry-run does
/// NOT touch posted_reviews and live posts are deduplicated by run-local id
/// or cross-run fingerprint.
/// </summary>
public class ReviewPublishOrchestratorTests : IAsyncLifetime
{
    private string _connString = string.Empty;
    private PrInboxDb _db = null!;
    private Microsoft.Data.Sqlite.SqliteConnection _keepAlive = null!;
    private PullRequestRepository _prRepo = null!;
    private PostedReviewRepository _postedRepo = null!;
    private const string Url = "https://github.com/octocat/playground/pull/5589";

    public async Task InitializeAsync()
    {
        _connString = PrInboxDb.InMemoryConnectionString($"orch-{Guid.NewGuid():N}");
        _db = new PrInboxDb(_connString);
        _keepAlive = await _db.OpenAsync();
        await new MigrationRunner().MigrateAsync(_connString);

        _prRepo = new PullRequestRepository(_db);
        _postedRepo = new PostedReviewRepository(_db);

        await _prRepo.UpsertAsync(SamplePrRow(), CancellationToken.None);
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    [Fact]
    public async Task DryRun_does_not_write_posted_reviews_and_lets_publisher_see_request()
    {
        var publisher = new RecordingPublisher(returnPosted: false);
        var selector = new FixedSelector(publisher);
        var orch = new ReviewPublishOrchestrator(
            selector, _prRepo, _postedRepo,
            NullLogger<ReviewPublishOrchestrator>.Instance);

        var req = MakeRequest(dryRun: true, findings: new[]
        {
            MakeFinding("f01"), MakeFinding("f02"),
        });

        var result = await orch.PublishAsync(req, CancellationToken.None);

        publisher.LastRequest.Should().NotBeNull();
        publisher.LastRequest!.DryRun.Should().BeTrue();
        publisher.LastRequest.Findings.Should().HaveCount(2);

        result.Errors.Should().BeEmpty();
        result.Posted.Should().BeFalse();
        result.SkippedAsAlreadyPosted.Should().Be(0);

        var rows = await _postedRepo.ListForPrAsync(SampleIdentity(), CancellationToken.None);
        rows.Should().BeEmpty("dry-run must not insert into posted_reviews");
    }

    [Fact]
    public async Task Live_post_writes_posted_reviews_then_second_post_skips_duplicates()
    {
        var runId = await CreateRunAsync();
        var publisher = new RecordingPublisher(returnPosted: true, reviewId: "999", reviewUrl: "https://gh/r/999");
        var selector = new FixedSelector(publisher);
        var orch = new ReviewPublishOrchestrator(
            selector, _prRepo, _postedRepo,
            NullLogger<ReviewPublishOrchestrator>.Instance);

        var r1 = await orch.PublishAsync(MakeRequest(
            dryRun: false,
            findings: new[] { MakeFinding("f01"), MakeFinding("f02") }, runId: runId), CancellationToken.None);

        r1.Posted.Should().BeTrue();
        r1.PlatformReviewId.Should().Be("999");
        r1.Errors.Should().BeEmpty();
        r1.Warnings.Should().BeEmpty($"insert into posted_reviews should not warn; got: {string.Join(" | ", r1.Warnings)}");
        var rows = await _postedRepo.ListForPrAsync(SampleIdentity(), CancellationToken.None);
        rows.Should().HaveCount(1);
        rows[0].FindingIds.Should().BeEquivalentTo(new[] { "f01", "f02" });
        rows[0].FindingFingerprints.Should().HaveCount(2);

        publisher.LastRequest!.Findings.Should().HaveCount(2);

        // Second post — same finding ids, plus one new one.
        var r2 = await orch.PublishAsync(MakeRequest(
            dryRun: false,
            findings: new[] { MakeFinding("f01"), MakeFinding("f02"), MakeFinding("f03") }, runId: runId), CancellationToken.None);

        r2.SkippedAsAlreadyPosted.Should().Be(2, "f01 and f02 are already in posted_reviews");
        publisher.LastRequest!.Findings.Should().HaveCount(1, "only f03 should reach the publisher");
        publisher.LastRequest.Findings[0].Id.Should().Be("f03");

        var r3 = await orch.PublishAsync(MakeRequest(
            dryRun: false,
            findings: new[] { MakeFinding("f01", file: "src/Edited.cs") }, runId: runId), CancellationToken.None);

        r3.SkippedAsAlreadyPosted.Should().Be(1, "the id was already posted in this run even though its fingerprint changed");
        publisher.LastRequest.Findings[0].Id.Should().Be("f03", "no third post should reach the publisher");
    }

    [Fact]
    public async Task New_run_reuses_id_for_different_finding_but_skips_matching_fingerprint()
    {
        var firstRun = await CreateRunAsync();
        var publisher = new RecordingPublisher(returnPosted: true);
        var orch = new ReviewPublishOrchestrator(
            new FixedSelector(publisher), _prRepo, _postedRepo,
            NullLogger<ReviewPublishOrchestrator>.Instance);

        var original = MakeFinding("f01");
        (await orch.PublishAsync(MakeRequest(false, new[] { original }, firstRun), CancellationToken.None))
            .Posted.Should().BeTrue();

        var secondRun = await CreateRunAsync();
        var newFinding = MakeFinding("f01", file: "src/Other.cs", line: 20);
        var next = await orch.PublishAsync(MakeRequest(false, new[] { newFinding }, secondRun), CancellationToken.None);

        next.Posted.Should().BeTrue();
        next.SkippedAsAlreadyPosted.Should().Be(0);
        publisher.LastRequest!.Findings.Should().ContainSingle().Which.Should().Be(newFinding);
        (await _postedRepo.ListForPrAsync(SampleIdentity(), CancellationToken.None))
            .Should().HaveCount(2);

        var sameIssueNewId = original with { Id = "f02" };
        var duplicate = await orch.PublishAsync(MakeRequest(false, new[] { sameIssueNewId }, secondRun), CancellationToken.None);
        duplicate.Posted.Should().BeFalse();
        duplicate.SkippedAsAlreadyPosted.Should().Be(1, "the file, line, and title match an earlier run");
        publisher.LastRequest.Findings.Should().ContainSingle().Which.Should().Be(newFinding);
    }

    [Fact]
    public async Task Posts_without_a_run_id_use_fingerprints_not_unscoped_ids()
    {
        var publisher = new RecordingPublisher(returnPosted: true);
        var orch = new ReviewPublishOrchestrator(
            new FixedSelector(publisher), _prRepo, _postedRepo,
            NullLogger<ReviewPublishOrchestrator>.Instance);
        (await orch.PublishAsync(MakeRequest(false, new[] { MakeFinding("f01") }), CancellationToken.None))
            .Posted.Should().BeTrue();

        var distinct = MakeFinding("f01", file: "src/Other.cs");
        var result = await orch.PublishAsync(MakeRequest(false, new[] { distinct }), CancellationToken.None);
        result.Posted.Should().BeTrue();
        result.SkippedAsAlreadyPosted.Should().Be(0);
        publisher.LastRequest!.Findings.Should().ContainSingle().Which.Should().Be(distinct);

        var duplicate = await orch.PublishAsync(MakeRequest(false, new[] { distinct }), CancellationToken.None);
        duplicate.SkippedAsAlreadyPosted.Should().Be(1);
    }

    [Fact]
    public async Task Persisted_history_does_not_suppress_reused_id_after_restart()
    {
        var file = Path.Combine(Path.GetTempPath(), $"pr-inbox-post-{Guid.NewGuid():N}.db");
        try
        {
            var connection = $"Data Source={file}";
            await new MigrationRunner().MigrateAsync(connection);
            var db = new PrInboxDb(connection);
            var prs = new PullRequestRepository(db);
            await prs.UpsertAsync(SamplePrRow(), CancellationToken.None);
            var runs = new ReviewRunRepository(db);
            var firstRun = await InsertRunAsync(runs);
            var publisher = new RecordingPublisher(returnPosted: true);
            var beforeRestart = new ReviewPublishOrchestrator(
                new FixedSelector(publisher), prs, new PostedReviewRepository(db),
                NullLogger<ReviewPublishOrchestrator>.Instance);
            (await beforeRestart.PublishAsync(MakeRequest(false, new[] { MakeFinding("f01") }, firstRun), CancellationToken.None))
                .Posted.Should().BeTrue();

            // Reopen the file-backed database with new repository/orchestrator instances.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var reopenedDb = new PrInboxDb(connection);
            var secondRun = await InsertRunAsync(new ReviewRunRepository(reopenedDb));
            var afterRestart = new ReviewPublishOrchestrator(
                new FixedSelector(publisher), new PullRequestRepository(reopenedDb),
                new PostedReviewRepository(reopenedDb),
                NullLogger<ReviewPublishOrchestrator>.Instance);
            var result = await afterRestart.PublishAsync(
                MakeRequest(false, new[] { MakeFinding("f01", file: "src/New.cs") }, secondRun),
                CancellationToken.None);

            result.Posted.Should().BeTrue();
            result.SkippedAsAlreadyPosted.Should().Be(0);
            (await new PostedReviewRepository(reopenedDb).ListForPrAsync(SampleIdentity(), CancellationToken.None))
                .Should().HaveCount(2);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task Unknown_pr_url_returns_failure_without_calling_publisher()
    {
        var publisher = new RecordingPublisher(returnPosted: true);
        var selector = new FixedSelector(publisher);
        var orch = new ReviewPublishOrchestrator(
            selector, _prRepo, _postedRepo,
            NullLogger<ReviewPublishOrchestrator>.Instance);

        var req = MakeRequest(dryRun: true, findings: new[] { MakeFinding("f01") })
            with { PrUrl = "https://github.com/never/seen/pull/1" };

        var result = await orch.PublishAsync(req, CancellationToken.None);
        result.Errors.Should().ContainSingle(e => e.Contains("not found in inbox"));
        publisher.LastRequest.Should().BeNull("publisher should never be called for unknown PR");
    }

    // -- helpers --

    private static PrIdentity SampleIdentity() => new(Url, "gh.com:0#5589");

    private static PullRequestRow SamplePrRow() => new(
        Identity: SampleIdentity(),
        SourceKind: SourceKind.GitHub,
        SourceId: "gh.com",
        DisplayRepo: "octocat/playground",
        Number: 5589,
        Title: "test PR",
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

    private Task<long> CreateRunAsync() => InsertRunAsync(new ReviewRunRepository(_db));

    private static Task<long> InsertRunAsync(ReviewRunRepository runs) =>
        runs.InsertAsync(
            SampleIdentity(), DateTimeOffset.UtcNow, "brief.md",
            $"run-{Guid.NewGuid():N}", "deadbeef", "base",
            ReviewRunStatus.Generated, null, CancellationToken.None);

    private static PublishRequest MakeRequest(bool dryRun, IReadOnlyList<FindingToPost> findings, long? runId = null) =>
        new(
            PrUrl: Url,
            RunId: runId,
            HeadShaAtAuthoring: "deadbeef",
            ReviewBodyHeader: "**review**",
            Findings: findings,
            DryRun: dryRun,
            ValidateRemoteState: false);

    private static FindingToPost MakeFinding(string id, string file = "src/A.cs", int line = 10) => new(
        Id: id,
        Severity: FindingSeverity.High,
        Confidence: FindingConfidence.High,
        FoundBy: new[] { "opus" },
        File: file,
        Line: line,
        LineEnd: null,
        DiffAnchorable: true,
        Title: $"issue {id}",
        Body: $"body for {id}",
        SuggestedInline: null);

    private sealed class RecordingPublisher : IPrReviewPublisher
    {
        private readonly bool _posted;
        private readonly string _reviewId;
        private readonly string? _reviewUrl;
        public PublishRequest? LastRequest { get; private set; }

        public RecordingPublisher(bool returnPosted, string reviewId = "1", string? reviewUrl = null)
        {
            _posted = returnPosted;
            _reviewId = reviewId;
            _reviewUrl = reviewUrl;
        }

        public string Kind => "fake";

        public Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken ct)
        {
            LastRequest = request;
            var inline = request.Findings.Count(f => f.DiffAnchorable);
            var body = request.Findings.Count - inline;
            if (request.DryRun)
            {
                return Task.FromResult(PublishResult.DryRunPlan(
                    inlineCount: inline,
                    bodyOnlyCount: body,
                    skipped: 0,
                    identityUsed: Kind,
                    warning: "dry-run"));
            }
            return Task.FromResult(new PublishResult(
                Posted: _posted,
                PlatformReviewId: _reviewId,
                ReviewUrl: _reviewUrl,
                InlineCount: inline,
                BodyOnlyCount: body,
                SkippedAsAlreadyPosted: 0,
                HeadShaAtPost: request.HeadShaAtAuthoring,
                HeadChanged: false,
                IdentityUsed: Kind,
                Warnings: Array.Empty<string>(),
                Errors: Array.Empty<string>()));
        }

        public Task<ThreadResolveResult> ResolveThreadsAsync(
            ThreadResolveRequest request, CancellationToken ct)
            => Task.FromResult(ThreadResolveResult.Failure(Kind, "Not implemented in test stub."));
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
