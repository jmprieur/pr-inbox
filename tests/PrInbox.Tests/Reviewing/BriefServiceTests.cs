using PrInbox.Core.Models;
using PrInbox.Core.Reviewing;
using PrInbox.Core.Storage;

namespace PrInbox.Tests.Reviewing;

/// <summary>
/// Tests for <see cref="BriefService.BuildBriefMarkdown"/>. These assert that
/// the dossier sections — author's framing, file table, CI/mergeable header,
/// thread anchors + body snippets — render the data we now carry through from
/// adapters and repositories. The agent's feedback called these out
/// specifically: the brief must give a reviewer everything they'd otherwise
/// fetch with a `gh pr view` + `gh pr diff` + `gh api comments` round trip.
/// </summary>
public class BriefServiceTests
{
    private static PullRequestRow MakePr(string? body = null, string? title = "Sample PR")
    {
        var id = new PrIdentity(
            Url: "https://github.com/owner/repo/pull/42",
            Stable: "gh.com:100#42");

        return new PullRequestRow(
            Identity: id,
            SourceKind: SourceKind.GitHub,
            SourceId: "gh.com",
            DisplayRepo: "owner/repo",
            Number: 42,
            Title: title,
            AuthorLogin: "octocat",
            Url: id.Url,
            Status: PullRequestStatus.Open,
            TrackingReason: TrackingReason.Assigned,
            IdentityUsed: "jmprieur",
            FirstSeenAt: DateTimeOffset.Parse("2026-05-13T20:00:00Z"),
            LastSyncedAt: DateTimeOffset.Parse("2026-05-13T20:30:00Z"),
            EnrichState: EnrichState.Enriched,
            LastBriefedHeadSha: null,
            LastReviewRunHeadSha: null,
            LastPostedReviewHeadSha: null,
            Body: body);
    }

    private static PrSnapshotRow MakeSnapshot(
        string? mergeableState = null,
        string? ciStatus = null,
        IReadOnlyList<SnapshotFileChange>? files = null)
    {
        return new PrSnapshotRow(
            Id: 1,
            Identity: new PrIdentity("https://github.com/owner/repo/pull/42", "gh.com:100#42"),
            SyncedAt: DateTimeOffset.Parse("2026-05-13T20:30:00Z"),
            HeadSha: "2e49b0c422d5e8f1a3b7c9d2e4f6a8b0c1d3e5f7",
            BaseSha: "8a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b",
            MergeBaseSha: null,
            OrderedCommitShas: new[] { "2e49b0c422d5e8f1a3b7c9d2e4f6a8b0c1d3e5f7" },
            ReviewerState: ReviewerState.Requested,
            PrState: PullRequestStatus.Open,
            RawMetadataJson: null,
            MergeableState: mergeableState,
            CiStatus: ciStatus,
            Files: files);
    }

    [Fact]
    public void Header_Includes_Ci_Mergeable_And_Size_When_Available()
    {
        var pr = MakePr();
        var snap = MakeSnapshot(
            mergeableState: "clean",
            ciStatus: "success",
            files: new[]
            {
                new SnapshotFileChange("src/A.cs", 10, 2, "modified"),
                new SnapshotFileChange("src/B.cs", 5, 0, "added"),
            });

        var md = BriefService.BuildBriefMarkdown(pr, snap,
            openThreads: Array.Empty<ObservedThreadRow>(),
            recentBotThreads: Array.Empty<ObservedThreadRow>(),
            priorRuns: Array.Empty<ReviewRunRow>(),
            runDir: @"C:\runs\x");

        Assert.Contains("CI: success", md);
        Assert.Contains("Mergeable: clean", md);
        Assert.Contains("Size: +15 / −2 across 2 file(s)", md);
    }

    [Fact]
    public void Header_Omits_Ci_And_Mergeable_When_Null()
    {
        var pr = MakePr();
        var snap = MakeSnapshot();

        var md = BriefService.BuildBriefMarkdown(pr, snap,
            openThreads: Array.Empty<ObservedThreadRow>(),
            recentBotThreads: Array.Empty<ObservedThreadRow>(),
            priorRuns: Array.Empty<ReviewRunRow>(),
            runDir: @"C:\runs\x");

        Assert.DoesNotContain("CI:", md);
        Assert.DoesNotContain("Mergeable:", md);
    }

    [Fact]
    public void Body_Renders_As_Blockquote_When_Present()
    {
        var pr = MakePr(body: "## Summary\nFirst line.\nSecond line.");
        var snap = MakeSnapshot();

        var md = BriefService.BuildBriefMarkdown(pr, snap,
            openThreads: Array.Empty<ObservedThreadRow>(),
            recentBotThreads: Array.Empty<ObservedThreadRow>(),
            priorRuns: Array.Empty<ReviewRunRow>(),
            runDir: @"C:\runs\x");

        Assert.Contains("## Author's framing", md);
        Assert.Contains("> ## Summary", md);
        Assert.Contains("> First line.", md);
        Assert.Contains("> Second line.", md);
    }

    [Fact]
    public void Body_Truncated_At_4KB_Cap()
    {
        var longBody = new string('x', 10_000);
        var pr = MakePr(body: longBody);
        var snap = MakeSnapshot();

        var md = BriefService.BuildBriefMarkdown(pr, snap,
            openThreads: Array.Empty<ObservedThreadRow>(),
            recentBotThreads: Array.Empty<ObservedThreadRow>(),
            priorRuns: Array.Empty<ReviewRunRow>(),
            runDir: @"C:\runs\x");

        Assert.Contains("## Author's framing", md);
        Assert.Contains("…", md);
        Assert.DoesNotContain(new string('x', 5000), md);
    }

    [Fact]
    public void Author_Framing_Section_Hidden_When_Body_Null()
    {
        var pr = MakePr(body: null);
        var snap = MakeSnapshot();

        var md = BriefService.BuildBriefMarkdown(pr, snap,
            openThreads: Array.Empty<ObservedThreadRow>(),
            recentBotThreads: Array.Empty<ObservedThreadRow>(),
            priorRuns: Array.Empty<ReviewRunRow>(),
            runDir: @"C:\runs\x");

        Assert.DoesNotContain("## Author's framing", md);
    }

    [Fact]
    public void Files_Section_Renders_Markdown_Table_When_Files_Present()
    {
        var pr = MakePr();
        var snap = MakeSnapshot(files: new[]
        {
            new SnapshotFileChange("src/Foo.cs", 30, 5, "modified"),
            new SnapshotFileChange("README.md", 2, 0, "modified"),
        });

        var md = BriefService.BuildBriefMarkdown(pr, snap,
            openThreads: Array.Empty<ObservedThreadRow>(),
            recentBotThreads: Array.Empty<ObservedThreadRow>(),
            priorRuns: Array.Empty<ReviewRunRow>(),
            runDir: @"C:\runs\x");

        Assert.Contains("## Files", md);
        Assert.Contains("| `src/Foo.cs` | 30 | 5 |", md);
        Assert.Contains("| `README.md` | 2 | 0 |", md);
    }

    [Fact]
    public void Files_Section_Notes_Unavailable_When_Null()
    {
        var pr = MakePr();
        var snap = MakeSnapshot(files: null);

        var md = BriefService.BuildBriefMarkdown(pr, snap,
            openThreads: Array.Empty<ObservedThreadRow>(),
            recentBotThreads: Array.Empty<ObservedThreadRow>(),
            priorRuns: Array.Empty<ReviewRunRow>(),
            runDir: @"C:\runs\x");

        Assert.Contains("## Files", md);
        Assert.Contains("_File list unavailable for this source._", md);
    }

    [Fact]
    public void Open_Thread_Renders_Anchor_And_Body_Snippet()
    {
        var pr = MakePr();
        var snap = MakeSnapshot();
        var thread = new ObservedThreadRow(
            Id: 1,
            Identity: pr.Identity,
            PlatformThreadId: "review-comment:1",
            Kind: ThreadKind.ReviewComment,
            AuthorLogin: "Copilot",
            IsBot: true,
            BotKind: BotKind.CopilotReview,
            FirstSeenAt: DateTimeOffset.Parse("2026-05-15T20:00:00Z"),
            LastSeenAt: DateTimeOffset.Parse("2026-05-15T20:00:00Z"),
            ResolvedAt: null,
            RawJson: null,
            LastCommentBody: "NuGet does NOT auto-update; this contradicts the shared guide.",
            AnchorPath: "src/Foo.cs",
            AnchorLine: 214);

        var md = BriefService.BuildBriefMarkdown(pr, snap,
            openThreads: new[] { thread },
            recentBotThreads: Array.Empty<ObservedThreadRow>(),
            priorRuns: Array.Empty<ReviewRunRow>(),
            runDir: @"C:\runs\x");

        Assert.Contains("@ `src/Foo.cs:214`", md);
        Assert.Contains("> NuGet does NOT auto-update", md);
    }

    [Fact]
    public async Task Bot_Pr_Callout_Renders_For_Sre_Agent_Title()
    {
        var pr = MakePr(title: "Generated by SRE Agent: ALAS daily skill improvements");
        var snap = MakeSnapshot();

        var md = BriefService.BuildBriefMarkdown(pr, snap,
            openThreads: Array.Empty<ObservedThreadRow>(),
            recentBotThreads: Array.Empty<ObservedThreadRow>(),
            priorRuns: Array.Empty<ReviewRunRow>(),
            runDir: @"C:\runs\x");

        Assert.Contains("**[Generated by SRE Agent]**", md);
    }

    [Theory]
    // Bots often put their banner as a literal bracketed prefix; the capture
    // must not greedily eat past the closing bracket. Regression for the
    // `**[Generated by SRE Agent] s360]**` rendering bug.
    [InlineData("[Generated by SRE Agent] s360-breeze-toolkit: ALAS daily skill improvements", "SRE Agent")]
    [InlineData("Generated by SRE Agent: subject", "SRE Agent")]
    [InlineData("Generated by Dependabot - bump foo from 1 to 2", "Dependabot")]
    public void DetectBotPr_Extracts_Tool_Name_Cleanly(string title, string expectedTool)
    {
        var (callout, _) = BriefService.DetectBotPr(title, author: "someone");
        Assert.NotNull(callout);
        Assert.Equal($"[Generated by {expectedTool}]", callout);
    }

    [Fact]
    public void DetectBotPr_Returns_Null_When_No_Bot_Signal()
    {
        var (callout, suffix) = BriefService.DetectBotPr("Refactor user service", "octocat");
        Assert.Null(callout);
        Assert.Equal(string.Empty, suffix);
    }

    [Fact]
    public void Output_Contract_References_Posting_Style_Sidecar()
    {
        var pr = MakePr();
        var snap = MakeSnapshot();

        var md = BriefService.BuildBriefMarkdown(pr, snap,
            openThreads: Array.Empty<ObservedThreadRow>(),
            recentBotThreads: Array.Empty<ObservedThreadRow>(),
            priorRuns: Array.Empty<ReviewRunRow>(),
            runDir: @"C:\runs\x");

        Assert.Contains("./posting-style.md", md);
    }

    [Fact]
    public void Output_Contract_Tells_Agent_It_Is_The_Reviewer()
    {
        var pr = MakePr();
        var snap = MakeSnapshot();

        var md = BriefService.BuildBriefMarkdown(pr, snap,
            openThreads: Array.Empty<ObservedThreadRow>(),
            recentBotThreads: Array.Empty<ObservedThreadRow>(),
            priorRuns: Array.Empty<ReviewRunRow>(),
            runDir: @"C:\runs\x");

        Assert.Contains("running **as** the dual-model-review agent", md);
        Assert.Contains("do not spawn another dual-model-review", md);
    }

    [Fact]
    public void Prior_Findings_For_Same_Head_Are_Surfaced_As_Callout()
    {
        var pr = MakePr();
        var snap = MakeSnapshot();

        // Create a temp run directory with a findings.yaml and a
        // matching HeadSha — the brief should call this out explicitly
        // so the reviewer re-affirms / supersedes / drops rather than
        // re-discovering.
        var tmp = Path.Combine(Path.GetTempPath(), "prinbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var findingsPath = Path.Combine(tmp, "findings.yaml");
        File.WriteAllText(findingsPath, "schema_version: 1\nfindings: []\n");
        try
        {
            var priorRun = new ReviewRunRow(
                Id: 7,
                Identity: pr.Identity,
                CreatedAt: DateTimeOffset.Parse("2026-05-13T20:00:00Z"),
                BriefPath: Path.Combine(tmp, "brief.md"),
                RunDirectory: tmp,
                HeadSha: snap.HeadSha,
                BaseSha: snap.BaseSha,
                Status: ReviewRunStatus.Generated,
                CopilotSessionId: null,
                Notes: null);

            var md = BriefService.BuildBriefMarkdown(pr, snap,
                openThreads: Array.Empty<ObservedThreadRow>(),
                recentBotThreads: Array.Empty<ObservedThreadRow>(),
                priorRuns: new[] { priorRun },
                runDir: @"C:\runs\x");

            Assert.Contains("Prior `findings.yaml` for current HEAD", md);
            Assert.Contains(findingsPath, md);
            Assert.Contains("re-affirm, supersede, or drop", md);
            // Run history is still listed alongside the callout.
            Assert.Contains("Run #7", md);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Prior_Findings_For_Different_Head_Are_Not_Surfaced_As_Same_Head_Callout()
    {
        var pr = MakePr();
        var snap = MakeSnapshot();

        var tmp = Path.Combine(Path.GetTempPath(), "prinbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        File.WriteAllText(Path.Combine(tmp, "findings.yaml"), "schema_version: 1\nfindings: []\n");
        try
        {
            var priorRun = new ReviewRunRow(
                Id: 8,
                Identity: pr.Identity,
                CreatedAt: DateTimeOffset.Parse("2026-05-13T20:00:00Z"),
                BriefPath: Path.Combine(tmp, "brief.md"),
                RunDirectory: tmp,
                HeadSha: "ffffffffffffffffffffffffffffffffffffffff", // different from snapshot HEAD
                BaseSha: snap.BaseSha,
                Status: ReviewRunStatus.Generated,
                CopilotSessionId: null,
                Notes: null);

            var md = BriefService.BuildBriefMarkdown(pr, snap,
                openThreads: Array.Empty<ObservedThreadRow>(),
                recentBotThreads: Array.Empty<ObservedThreadRow>(),
                priorRuns: new[] { priorRun },
                runDir: @"C:\runs\x");

            Assert.DoesNotContain("Prior `findings.yaml` for current HEAD", md);
            // But the run is still listed in the history line.
            Assert.Contains("Run #8", md);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }
}
