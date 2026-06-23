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
}
