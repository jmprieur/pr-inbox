using PrInbox.Core.Models;
using PrInbox.Core.Storage;
using PrInbox.Sources;
using PrInbox.Sources.Fakes;

namespace PrInbox.Tests.Sources;

/// <summary>
/// Behavioral tests for the authored ("My PRs") pass added to
/// <see cref="SyncOrchestrator.RunFastAsync"/>: role tagging
/// (<see cref="MyRole"/>), the <c>not_reviewer</c> tracking sentinel, the
/// reviewer/authored view separation, and the role-scoped reviewer reconcile.
/// </summary>
public class SyncOrchestratorAuthoredTests : IAsyncLifetime
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
        _connString = PrInboxDb.InMemoryConnectionString($"orch-authored-{Guid.NewGuid():N}");
        _db = new PrInboxDb(_connString);
        _keepAlive = await _db.OpenAsync();
        await new MigrationRunner().MigrateAsync(_connString);

        _prs = new PullRequestRepository(_db);
        _snaps = new PrSnapshotRepository(_db);
        _threads = new ObservedThreadRepository(_db);
        _syncRuns = new SyncRunRepository(_db);
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    [Fact]
    public async Task RunFast_TagsAuthoredPrs_AsAuthor_NotReviewer_AndSeparatesViews()
    {
        var reviewerId = new PrIdentity("https://github.com/owner/repo/pull/1", "gh.com:100#1000");
        var authoredId = new PrIdentity("https://github.com/owner/repo/pull/9", "gh.com:100#9000");

        var source = new FakePrReadSourceBuilder("gh.com:emu", SourceKind.GitHub)
            .WithPullRequest(BasicPr(reviewerId), Detail(reviewerId))
            .WithAuthoredPullRequest(BasicPr(authoredId))
            .Build();
        var orch = new SyncOrchestrator(source, _prs, _snaps, _threads, _syncRuns);

        await orch.RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        var reviewer = await _prs.GetAsync(reviewerId.Url, CancellationToken.None);
        reviewer!.MyRole.Should().Be(MyRole.Reviewer);
        reviewer.TrackingReason.Should().Be(TrackingReason.Assigned);

        var authored = await _prs.GetAsync(authoredId.Url, CancellationToken.None);
        authored!.MyRole.Should().Be(MyRole.Author);
        authored.TrackingReason.Should().Be(TrackingReason.NotReviewer);

        // The authored view shows only authored PRs...
        var authoredList = await _prs.ListAuthoredAsync(CancellationToken.None);
        authoredList.Select(r => r.Url).Should().BeEquivalentTo(new[] { authoredId.Url });

        // ...and the reviewer inbox (ListActive) excludes author-only rows.
        var reviewerList = await _prs.ListActiveAsync(CancellationToken.None);
        reviewerList.Select(r => r.Url).Should().Contain(reviewerId.Url);
        reviewerList.Select(r => r.Url).Should().NotContain(authoredId.Url);
    }

    [Fact]
    public async Task RunFast_PrSeenInBothPasses_BecomesBoth_AndAppearsInBothViews()
    {
        var id = new PrIdentity("https://github.com/owner/repo/pull/3", "gh.com:100#3000");
        var pr = BasicPr(id);

        var source = new FakePrReadSourceBuilder("gh.com:emu", SourceKind.GitHub)
            .WithPullRequest(pr, Detail(id))   // reviewer pass
            .WithAuthoredPullRequest(pr)        // authored pass (same identity)
            .Build();
        var orch = new SyncOrchestrator(source, _prs, _snaps, _threads, _syncRuns);

        await orch.RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        var row = await _prs.GetAsync(id.Url, CancellationToken.None);
        row!.MyRole.Should().Be(MyRole.Both);
        // Reviewer lifecycle still applies for a Both PR.
        row.TrackingReason.Should().Be(TrackingReason.Assigned);

        (await _prs.ListActiveAsync(CancellationToken.None)).Select(r => r.Url).Should().Contain(id.Url);
        (await _prs.ListAuthoredAsync(CancellationToken.None)).Select(r => r.Url).Should().Contain(id.Url);
    }

    [Fact]
    public async Task ReviewerReconcile_DemotesDisappearedReviewer_ButLeavesAuthorOnlyRows()
    {
        var reviewerId = new PrIdentity("https://github.com/owner/repo/pull/1", "gh.com:100#1000");
        var authoredId = new PrIdentity("https://github.com/owner/repo/pull/9", "gh.com:100#9000");

        // T0: a reviewer PR and an authored PR.
        var t0 = new FakePrReadSourceBuilder("gh.com:emu", SourceKind.GitHub)
            .WithPullRequest(BasicPr(reviewerId), Detail(reviewerId))
            .WithAuthoredPullRequest(BasicPr(authoredId))
            .Build();
        await new SyncOrchestrator(t0, _prs, _snaps, _threads, _syncRuns)
            .RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        // T1: the reviewer PR is gone from the reviewer query; the authored PR remains.
        var t1 = new FakePrReadSourceBuilder("gh.com:emu", SourceKind.GitHub)
            .WithAuthoredPullRequest(BasicPr(authoredId))
            .Build();
        await new SyncOrchestrator(t1, _prs, _snaps, _threads, _syncRuns)
            .RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        // The vanished reviewer PR is demoted by the reviewer reconcile.
        var reviewer = await _prs.GetAsync(reviewerId.Url, CancellationToken.None);
        reviewer!.TrackingReason.Should().Be(TrackingReason.PreviouslyAssigned);

        // The author-only PR is untouched by the reviewer reconcile.
        var authored = await _prs.GetAsync(authoredId.Url, CancellationToken.None);
        authored!.MyRole.Should().Be(MyRole.Author);
        authored.TrackingReason.Should().Be(TrackingReason.NotReviewer);
    }

    private static RemotePullRequest BasicPr(PrIdentity id) =>
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
            LastUpdated: DateTimeOffset.Parse("2026-05-13T10:00:00Z"));

    private static RemotePullRequestDetail Detail(PrIdentity id) =>
        new(
            Identity: id,
            HeadSha: "abc1234567890aaa",
            BaseSha: "base000000000000",
            MergeBaseSha: null,
            OrderedCommitShas: new[] { "abc1234567890aaa" },
            ReviewerState: ReviewerState.Requested,
            Status: PullRequestStatus.Open,
            RawMetadataJson: "{}");
}
