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
/// NOT touch posted_reviews, live mode does, and a second publish of the
/// same findings skips them via per-finding idempotency.
/// </summary>
public class ReviewPublishOrchestratorTests : IAsyncLifetime
{
    private string _connString = string.Empty;
    private PrInboxDb _db = null!;
    private Microsoft.Data.Sqlite.SqliteConnection _keepAlive = null!;
    private PullRequestRepository _prRepo = null!;
    private PostedReviewRepository _postedRepo = null!;
    private const string Url = "https://github.com/agency-microsoft/playground/pull/5589";

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
        var publisher = new RecordingPublisher(returnPosted: true, reviewId: "999", reviewUrl: "https://gh/r/999");
        var selector = new FixedSelector(publisher);
        var orch = new ReviewPublishOrchestrator(
            selector, _prRepo, _postedRepo,
            NullLogger<ReviewPublishOrchestrator>.Instance);

        var r1 = await orch.PublishAsync(MakeRequest(
            dryRun: false,
            findings: new[] { MakeFinding("f01"), MakeFinding("f02") }), CancellationToken.None);

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
            findings: new[] { MakeFinding("f01"), MakeFinding("f02"), MakeFinding("f03") }), CancellationToken.None);

        r2.SkippedAsAlreadyPosted.Should().Be(2, "f01 and f02 are already in posted_reviews");
        publisher.LastRequest!.Findings.Should().HaveCount(1, "only f03 should reach the publisher");
        publisher.LastRequest.Findings[0].Id.Should().Be("f03");
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
        DisplayRepo: "agency-microsoft/playground",
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

    private static PublishRequest MakeRequest(bool dryRun, IReadOnlyList<FindingToPost> findings) =>
        new(
            PrUrl: Url,
            RunId: null,
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
