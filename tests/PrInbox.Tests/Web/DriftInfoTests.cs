using PrInbox.Core.Models;
using PrInbox.Core.Storage;
using PrInbox.Web.Services;

namespace PrInbox.Tests.Web;

/// <summary>
/// Tests for <see cref="DriftInfo.Compute"/> and
/// <see cref="DriftInfo.BuildCompareUrl"/>. The drift summary is what
/// drives the inbox HEAD-changed chip and the Review-page banner — the
/// classification must be precise because both surfaces are silent on
/// the <see cref="DriftKind.Clean"/> case.
/// </summary>
public class DriftInfoTests
{
    private const string ShaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ShaB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ShaC = "cccccccccccccccccccccccccccccccccccccccc";
    private const string ShaD = "dddddddddddddddddddddddddddddddddddddddd";

    private static PullRequestRow MakePr(string? lastReviewRunSha) =>
        new PullRequestRow(
            Identity: new PrIdentity("https://github.com/owner/repo/pull/42", "gh.com:100#42"),
            SourceKind: SourceKind.GitHub,
            SourceId: "gh.com",
            DisplayRepo: "owner/repo",
            Number: 42,
            Title: "Sample",
            AuthorLogin: "octo",
            Url: "https://github.com/owner/repo/pull/42",
            Status: PullRequestStatus.Open,
            TrackingReason: TrackingReason.Assigned,
            IdentityUsed: "jmprieur",
            FirstSeenAt: DateTimeOffset.Parse("2026-05-13T20:00:00Z"),
            LastSyncedAt: DateTimeOffset.Parse("2026-05-13T20:30:00Z"),
            EnrichState: EnrichState.Enriched,
            LastBriefedHeadSha: null,
            LastReviewRunHeadSha: lastReviewRunSha,
            LastPostedReviewHeadSha: null,
            Body: null);

    private static PrSnapshotRow MakeSnap(string headSha, IReadOnlyList<string> commits) =>
        new PrSnapshotRow(
            Id: 1,
            Identity: new PrIdentity("https://github.com/owner/repo/pull/42", "gh.com:100#42"),
            SyncedAt: DateTimeOffset.Parse("2026-05-13T20:30:00Z"),
            HeadSha: headSha,
            BaseSha: "0000000000000000000000000000000000000000",
            MergeBaseSha: null,
            OrderedCommitShas: commits,
            ReviewerState: ReviewerState.Requested,
            PrState: PullRequestStatus.Open,
            RawMetadataJson: null);

    [Fact]
    public void NeverReviewed_When_LastReviewRunHeadSha_Is_Null()
    {
        var pr = MakePr(lastReviewRunSha: null);
        var snap = MakeSnap(ShaA, new[] { ShaA });

        var drift = DriftInfo.Compute(pr, snap);

        Assert.Equal(DriftKind.NeverReviewed, drift.Kind);
        Assert.Equal(0, drift.CommitsAhead);
        Assert.Null(drift.LastReviewedHeadSha);
        Assert.Equal(ShaA, drift.CurrentHeadSha);
    }

    [Fact]
    public void Unknown_When_Snapshot_Missing()
    {
        var pr = MakePr(lastReviewRunSha: ShaA);

        var drift = DriftInfo.Compute(pr, snap: null);

        Assert.Equal(DriftKind.Unknown, drift.Kind);
        Assert.Equal(ShaA, drift.LastReviewedHeadSha);
        Assert.Null(drift.CurrentHeadSha);
    }

    [Fact]
    public void Clean_When_Sha_Matches_Current_Head()
    {
        var pr = MakePr(lastReviewRunSha: ShaA);
        var snap = MakeSnap(ShaA, new[] { ShaA });

        var drift = DriftInfo.Compute(pr, snap);

        Assert.Equal(DriftKind.Clean, drift.Kind);
        Assert.Equal(0, drift.CommitsAhead);
    }

    [Fact]
    public void Ahead_By_One_When_Anchor_Is_One_Commit_Back()
    {
        // newest-first: [B, A] → HEAD = B, anchor A at idx 1.
        var pr = MakePr(lastReviewRunSha: ShaA);
        var snap = MakeSnap(ShaB, new[] { ShaB, ShaA });

        var drift = DriftInfo.Compute(pr, snap);

        Assert.Equal(DriftKind.Ahead, drift.Kind);
        Assert.Equal(1, drift.CommitsAhead);
        Assert.Equal(ShaA, drift.LastReviewedHeadSha);
        Assert.Equal(ShaB, drift.CurrentHeadSha);
    }

    [Fact]
    public void Ahead_By_Three_When_Anchor_Is_Three_Commits_Back()
    {
        // newest-first: [D, C, B, A] → anchor A at idx 3 → +3 commits.
        var pr = MakePr(lastReviewRunSha: ShaA);
        var snap = MakeSnap(ShaD, new[] { ShaD, ShaC, ShaB, ShaA });

        var drift = DriftInfo.Compute(pr, snap);

        Assert.Equal(DriftKind.Ahead, drift.Kind);
        Assert.Equal(3, drift.CommitsAhead);
    }

    [Fact]
    public void ForcePushed_When_Anchor_Not_In_Commit_List()
    {
        // HEAD = D, commits [D, C, B] — A is missing → force-pushed.
        var pr = MakePr(lastReviewRunSha: ShaA);
        var snap = MakeSnap(ShaD, new[] { ShaD, ShaC, ShaB });

        var drift = DriftInfo.Compute(pr, snap);

        Assert.Equal(DriftKind.ForcePushed, drift.Kind);
        Assert.Equal(0, drift.CommitsAhead);
        Assert.Equal(ShaA, drift.LastReviewedHeadSha);
        Assert.Equal(ShaD, drift.CurrentHeadSha);
    }

    [Fact]
    public void Unknown_When_Commit_List_Empty_But_Shas_Differ()
    {
        // Edge case: snap exists but somehow has no commit list. We can't
        // distinguish "ahead by N" from "force-pushed", so we degrade to
        // Unknown rather than guess wrong.
        var pr = MakePr(lastReviewRunSha: ShaA);
        var snap = MakeSnap(ShaB, Array.Empty<string>());

        var drift = DriftInfo.Compute(pr, snap);

        Assert.Equal(DriftKind.Unknown, drift.Kind);
    }

    [Fact]
    public void BuildCompareUrl_Returns_Github_Compare_Url()
    {
        var drift = new DriftInfo(DriftKind.Ahead, 2, ShaA, ShaB);
        var url = drift.BuildCompareUrl("https://github.com/owner/repo/pull/42");

        Assert.Equal($"https://github.com/owner/repo/compare/{ShaA}...{ShaB}", url);
    }

    [Fact]
    public void BuildCompareUrl_Returns_Github_Enterprise_Compare_Url()
    {
        // Enterprise URLs share the same /pull/N path shape.
        var drift = new DriftInfo(DriftKind.Ahead, 1, ShaA, ShaB);
        var url = drift.BuildCompareUrl("https://github.proxima.example/myorg/myrepo/pull/7");

        Assert.Equal($"https://github.proxima.example/myorg/myrepo/compare/{ShaA}...{ShaB}", url);
    }

    [Fact]
    public void BuildCompareUrl_Returns_Null_For_Non_Github_Url()
    {
        // ADO URLs use a different shape; we don't synthesize a branch
        // compare for them today (project context is needed and not
        // always present in the PR URL).
        var drift = new DriftInfo(DriftKind.Ahead, 1, ShaA, ShaB);
        var url = drift.BuildCompareUrl("https://dev.azure.com/org/proj/_git/repo/pullrequest/123");

        Assert.Null(url);
    }

    [Fact]
    public void BuildCompareUrl_Returns_Null_When_Shas_Missing()
    {
        var noShas = new DriftInfo(DriftKind.NeverReviewed, 0, null, null);
        var noCurrent = new DriftInfo(DriftKind.Unknown, 0, ShaA, null);

        Assert.Null(noShas.BuildCompareUrl("https://github.com/owner/repo/pull/42"));
        Assert.Null(noCurrent.BuildCompareUrl("https://github.com/owner/repo/pull/42"));
    }

    [Fact]
    public void Compute_With_String_Anchor_Mirrors_Row_Overload()
    {
        // The two Compute overloads should agree when given equivalent
        // inputs — the review-page banner relies on the string anchor
        // path, the inbox relies on the row anchor path.
        var pr = MakePr(lastReviewRunSha: ShaA);
        var snap = MakeSnap(ShaB, new[] { ShaB, ShaA });

        var fromRow = DriftInfo.Compute(pr, snap);
        var fromSha = DriftInfo.Compute(ShaA, snap);

        Assert.Equal(fromRow.Kind, fromSha.Kind);
        Assert.Equal(fromRow.CommitsAhead, fromSha.CommitsAhead);
        Assert.Equal(fromRow.LastReviewedHeadSha, fromSha.LastReviewedHeadSha);
        Assert.Equal(fromRow.CurrentHeadSha, fromSha.CurrentHeadSha);
    }
}
