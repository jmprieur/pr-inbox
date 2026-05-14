using PrInbox.Core.Models;
using PrInbox.Sources;
using PrInbox.Sources.Fakes;

namespace PrInbox.Tests.Sources;

/// <summary>
/// Characterization tests for the <see cref="IPrReadSource"/> contract using
/// the in-memory <see cref="FakePrReadSource"/>. These tests assert the
/// shape and invariants that every real adapter must also satisfy.
/// </summary>
public class IPrReadSourceContractTests
{
    private static readonly PrIdentity IdAlpha = new(
        Display: "gh.com:owner/repo#1",
        Stable: "gh.com:100#1000");

    private static readonly PrIdentity IdBeta = new(
        Display: "gh.com:owner/repo#2",
        Stable: "gh.com:100#2000");

    [Fact]
    public async Task GetReviewInbox_Returns_All_Configured_Prs()
    {
        var source = BuildSourceWithTwoPrs();

        var inbox = await source.GetReviewInboxAsync(CancellationToken.None);

        inbox.Should().HaveCount(2);
        inbox.Select(p => p.Identity).Should().BeEquivalentTo(new[] { IdAlpha, IdBeta });
    }

    [Fact]
    public async Task GetPullRequestDetail_Returns_Snapshot_Data_For_Known_Pr()
    {
        var source = BuildSourceWithTwoPrs();

        var detail = await source.GetPullRequestDetailAsync(IdAlpha, CancellationToken.None);

        detail.Identity.Should().Be(IdAlpha);
        detail.HeadSha.Should().Be("abc1234567890aaa");
        detail.BaseSha.Should().Be("base000000000000");
        detail.OrderedCommitShas.Should().HaveCount(3);
    }

    [Fact]
    public async Task GetPullRequestDetail_Throws_For_Unknown_Pr()
    {
        var source = BuildSourceWithTwoPrs();
        var unknown = new PrIdentity("gh.com:owner/repo#999", "gh.com:100#999000");

        Func<Task> act = () => source.GetPullRequestDetailAsync(unknown, CancellationToken.None);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Compare_Same_Sha_Reports_No_Change()
    {
        var source = BuildSourceWithTwoPrs();

        var result = await source.CompareAsync(IdAlpha, "abc1234567890aaa", "abc1234567890aaa", CancellationToken.None);

        result.BaseUnreachableFromHead.Should().BeFalse();
        result.CommitsAhead.Should().Be(0);
        result.CommitsBehind.Should().Be(0);
    }

    [Fact]
    public async Task Compare_Unknown_Previous_Sha_Reports_Force_Push()
    {
        var source = BuildSourceWithTwoPrs();

        var result = await source.CompareAsync(
            IdAlpha,
            previousHeadSha: "deadbeefdeadbeef",
            currentHeadSha: "abc1234567890aaa",
            CancellationToken.None);

        result.BaseUnreachableFromHead.Should().BeTrue();
        result.CommitsBehind.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetThreads_Returns_Configured_Threads_With_Bot_Flag_Preserved()
    {
        var source = BuildSourceWithTwoPrs();

        var threads = await source.GetThreadsAsync(IdAlpha, CancellationToken.None);

        threads.Should().HaveCount(2);
        threads.Should().ContainSingle(t => t.IsBot && t.BotKind == BotKind.CopilotReview);
    }

    [Fact]
    public void Capabilities_For_GitHub_Default_Includes_Global_Inbox()
    {
        var source = new FakePrReadSourceBuilder("gh.com", SourceKind.GitHub).Build();
        source.Capabilities.SupportsGlobalReviewerInbox.Should().BeTrue();
    }

    [Fact]
    public void Capabilities_For_AzureDevOps_Default_Has_No_Global_Inbox()
    {
        var source = new FakePrReadSourceBuilder("ado:mseng", SourceKind.AzureDevOps).Build();
        source.Capabilities.SupportsGlobalReviewerInbox.Should().BeFalse();
    }

    private static FakePrReadSource BuildSourceWithTwoPrs()
    {
        var prAlpha = new RemotePullRequest(
            Identity: IdAlpha,
            SourceKind: SourceKind.GitHub,
            SourceId: "gh.com",
            DisplayRepo: "owner/repo",
            Number: 1,
            Title: "Sample PR Alpha",
            AuthorLogin: "octocat",
            Url: "https://github.com/owner/repo/pull/1",
            Status: PullRequestStatus.Open,
            LastUpdated: DateTimeOffset.Parse("2026-05-13T10:00:00Z"));

        var detailAlpha = new RemotePullRequestDetail(
            Identity: IdAlpha,
            HeadSha: "abc1234567890aaa",
            BaseSha: "base000000000000",
            MergeBaseSha: "merge00000000000",
            OrderedCommitShas: new[] { "abc1234567890aaa", "abc1234567890bbb", "abc1234567890ccc" },
            ReviewerState: ReviewerState.Requested,
            Status: PullRequestStatus.Open,
            RawMetadataJson: "{}");

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
            new RemoteThread(
                PlatformThreadId: "t-2",
                Kind: ThreadKind.ReviewComment,
                AuthorLogin: "copilot-pull-request-reviewer[bot]",
                IsBot: true,
                BotKind: BotKind.CopilotReview,
                IsResolved: false,
                CreatedAt: DateTimeOffset.Parse("2026-05-13T11:15:00Z"),
                LastUpdatedAt: DateTimeOffset.Parse("2026-05-13T11:15:00Z"),
                RawJson: "{}"),
        };

        var commitsAlpha = new[]
        {
            new RemoteCommit("abc1234567890aaa", "jmprieur", DateTimeOffset.Parse("2026-05-13T09:55:00Z"), "Third"),
            new RemoteCommit("abc1234567890bbb", "jmprieur", DateTimeOffset.Parse("2026-05-13T09:30:00Z"), "Second"),
            new RemoteCommit("abc1234567890ccc", "jmprieur", DateTimeOffset.Parse("2026-05-13T09:00:00Z"), "First"),
        };

        var prBeta = new RemotePullRequest(
            Identity: IdBeta,
            SourceKind: SourceKind.GitHub,
            SourceId: "gh.com",
            DisplayRepo: "owner/repo",
            Number: 2,
            Title: "Sample PR Beta",
            AuthorLogin: "octocat",
            Url: "https://github.com/owner/repo/pull/2",
            Status: PullRequestStatus.Open,
            LastUpdated: DateTimeOffset.Parse("2026-05-13T08:00:00Z"));

        var detailBeta = new RemotePullRequestDetail(
            Identity: IdBeta,
            HeadSha: "feeddead00000000",
            BaseSha: "base000000000000",
            MergeBaseSha: null,
            OrderedCommitShas: new[] { "feeddead00000000" },
            ReviewerState: ReviewerState.Requested,
            Status: PullRequestStatus.Open,
            RawMetadataJson: "{}");

        return new FakePrReadSourceBuilder("gh.com", SourceKind.GitHub)
            .WithPullRequest(prAlpha, detailAlpha, threadsAlpha, commitsAlpha)
            .WithPullRequest(prBeta, detailBeta)
            .Build();
    }
}
