using PrInbox.Core.Credentials;
using PrInbox.Web.Services;

namespace PrInbox.Tests.Web;

/// <summary>
/// Tests for the review-window tab title format
/// (<c>&lt;author&gt; &lt;repo&gt; #&lt;number&gt; @&lt;sha&gt; &lt;HH:mm&gt;</c>).
/// </summary>
public class ReviewLauncherTitleTests
{
    // Microsoft-style taxonomy for the tests: EMU + Public on github.com.
    private static readonly IReadOnlyList<IdentityClass> Classes = new[]
    {
        new IdentityClass { Name = "EMU", Host = "github.com", AliasSuffix = "_microsoft" },
        new IdentityClass { Name = "Public", Host = "github.com", AliasSuffix = "" },
    };

    [Theory]
    [InlineData("octocat", "octocat/playground", "octocat playground #8114 @ff2dcab 15:46")]
    [InlineData("jean-marc.prieur@example.com", "octocat/playground", "jean-marc playground #8114 @ff2dcab 15:46")]
    [InlineData("alice@example.com", "Context/MyRepo", "alice MyRepo #8114 @ff2dcab 15:46")]
    public void BuildTabTitle_LeadsWithAuthor_ThenRepoNameOnly(string author, string repo, string expected)
    {
        ReviewLauncher.BuildTabTitle(author, repo, 8114, "ff2dcab", "15:46", Classes)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildTabTitle_FallsBackToRepoFirst_WhenAuthorUnknown(string? author)
    {
        ReviewLauncher.BuildTabTitle(author, "octocat/playground", 8114, "ff2dcab", "15:46", Classes)
            .Should().Be("playground #8114 @ff2dcab 15:46");
    }

    [Theory]
    [InlineData("octocat", "octocat")]
    [InlineData("alice@example.com", "alice")]
    [InlineData("jean-marc.prieur@example.com", "jean-marc")]
    [InlineData("jmprieur_microsoft", "jmprieur")]
    [InlineData("jmprieur_microsoft@northeurope.com", "jmprieur")]
    [InlineData("_microsoft", "_microsoft")]
    [InlineData(null, "")]
    public void ShortAuthor_DerivesFirstNameOrAlias(string? login, string expected)
    {
        ReviewLauncher.ShortAuthor(login, Classes).Should().Be(expected);
    }

    [Fact]
    public void ShortAuthor_StripsOnlyMatchingClassSuffix()
    {
        var acme = new[] { new IdentityClass { Name = "Corp", Host = "github.com", AliasSuffix = "_acme" } };
        ReviewLauncher.ShortAuthor("dev_acme", acme).Should().Be("dev");          // matching suffix stripped
        ReviewLauncher.ShortAuthor("dev_acme", Classes).Should().Be("dev_acme");  // non-matching suffix kept
        ReviewLauncher.ShortAuthor("dev_microsoft", System.Array.Empty<IdentityClass>())
            .Should().Be("dev_microsoft");                                        // no classes = no stripping
    }

    [Theory]
    [InlineData("octocat/playground", "playground")]
    [InlineData("Context/MyRepo", "MyRepo")]
    [InlineData("no-slash-repo", "no-slash-repo")]
    public void ShortRepo_DropsOwnerPrefix(string displayRepo, string expected)
    {
        ReviewLauncher.ShortRepo(displayRepo).Should().Be(expected);
    }

    [Fact]
    public void BuildWtArguments_WindowMode_UsesNewWindow()
    {
        var args = ReviewLauncher.BuildWtArguments(
            tabPerReview: false, "octocat playground #1 [pr-inbox:run-7]", " --tabColor \"#5da4ff\"",
            @"C:\runs\7", @"C:\tools\launch-review.ps1", "-RunDirectory \"C:\\runs\\7\"");

        args.Should().StartWith("-w new nt ");
        args.Should().NotContain(ReviewLauncherSettings.ReviewWindowName);
    }

    [Fact]
    public void BuildWtArguments_TabMode_RoutesToSharedNamedWindow()
    {
        var args = ReviewLauncher.BuildWtArguments(
            tabPerReview: true, "octocat playground #1 [pr-inbox:run-7]", " --tabColor \"#5da4ff\"",
            @"C:\runs\7", @"C:\tools\launch-review.ps1", "-RunDirectory \"C:\\runs\\7\"");

        args.Should().StartWith($"-w {ReviewLauncherSettings.ReviewWindowName} nt ");
        args.Should().NotContain("-w new ");
    }

    [Theory]
    // Allowed characters survive verbatim — covers every char in the allowlist.
    [InlineData("alice repo #42 @ab12cd 15:46", "alice repo #42 @ab12cd 15:46")]
    [InlineData("a-z_./+:#@ 09", "a-z_./+:#@ 09")]
    // wt.exe sub-command separator: a `;` inside --title is still a delimiter
    // (wt re-splits every argv element on unescaped `;`). Must be neutralised.
    [InlineData("x; calc ;y", "x_ calc _y")]
    // cmd.exe metacharacters reachable via the `cmd /c start` fallback.
    [InlineData("a&b|c^d<e>f%g", "a_b_c_d_e_f_g")]
    // Quote / backslash / backtick — would break wt or pwsh quoting.
    [InlineData("a\"b\\c`d", "a_b_c_d")]
    // PowerShell sub-expression and parens.
    [InlineData("$(evil) (x)", "__evil_ _x_")]
    // ADO free-form display name attempting wt sub-command injection.
    [InlineData("bot; powershell -enc AAA ;x repo #1 @ab 12:00",
                "bot_ powershell -enc AAA _x repo #1 @ab 12:00")]
    public void SanitizeForShellTitle_AllowlistsSafeChars(string input, string expected)
    {
        ReviewLauncher.SanitizeForShellTitle(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("&|^;")]            // collapses to "____" → not whitespace, kept as-is
    public void SanitizeForShellTitle_FallsBackOnEmpty(string? input)
    {
        var result = ReviewLauncher.SanitizeForShellTitle(input);
        if (string.IsNullOrWhiteSpace(input))
        {
            result.Should().Be("pr-inbox review");
        }
        else
        {
            // Non-whitespace input never returns the fallback even if every
            // character is replaced — a string of underscores is still a
            // valid, harmless title.
            result.Should().Be("____");
        }
    }

    // ---- RehydrateInFlightRuns candidate precedence (IsBetterCandidate) ----

    private static readonly DateTimeOffset Older = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Newer = new(2026, 6, 2, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IsBetterCandidate_CompletedRun_BeatsNewerAbandonedRun()
    {
        // The regression that stranded badges on restart: a newer, empty
        // (abandoned) launch must NOT displace an older completed run.
        ReviewLauncher.IsBetterCandidate(
            candidateHasFindings: false, candidateCreated: Newer,
            incumbentHasFindings: true, incumbentCreated: Older)
            .Should().BeFalse();

        // ...and the completed run replaces a newer abandoned incumbent.
        ReviewLauncher.IsBetterCandidate(
            candidateHasFindings: true, candidateCreated: Older,
            incumbentHasFindings: false, incumbentCreated: Newer)
            .Should().BeTrue();
    }

    [Fact]
    public void IsBetterCandidate_WithinSameClass_NewerWins()
    {
        // Both completed → newest completed run wins.
        ReviewLauncher.IsBetterCandidate(true, Newer, true, Older).Should().BeTrue();
        ReviewLauncher.IsBetterCandidate(true, Older, true, Newer).Should().BeFalse();

        // Both abandoned (no PR has completed yet) → newest in-flight dir wins
        // so its watcher catches findings when they land.
        ReviewLauncher.IsBetterCandidate(false, Newer, false, Older).Should().BeTrue();
        ReviewLauncher.IsBetterCandidate(false, Older, false, Newer).Should().BeFalse();
    }
}
