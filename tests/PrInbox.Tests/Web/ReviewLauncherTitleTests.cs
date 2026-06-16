using PrInbox.Web.Services;

namespace PrInbox.Tests.Web;

/// <summary>
/// Tests for the review-window tab title format
/// (<c>&lt;author&gt; &lt;repo&gt; #&lt;number&gt; @&lt;sha&gt; &lt;HH:mm&gt;</c>).
/// </summary>
public class ReviewLauncherTitleTests
{
    [Theory]
    [InlineData("octocat", "agency-microsoft/playground", "octocat playground #8114 @ff2dcab 15:46")]
    [InlineData("jean-marc.prieur@microsoft.com", "agency-microsoft/playground", "jean-marc playground #8114 @ff2dcab 15:46")]
    [InlineData("alice@example.com", "Context/MyRepo", "alice MyRepo #8114 @ff2dcab 15:46")]
    public void BuildTabTitle_LeadsWithAuthor_ThenRepoNameOnly(string author, string repo, string expected)
    {
        ReviewLauncher.BuildTabTitle(author, repo, 8114, "ff2dcab", "15:46")
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildTabTitle_FallsBackToRepoFirst_WhenAuthorUnknown(string? author)
    {
        ReviewLauncher.BuildTabTitle(author, "agency-microsoft/playground", 8114, "ff2dcab", "15:46")
            .Should().Be("playground #8114 @ff2dcab 15:46");
    }

    [Theory]
    [InlineData("octocat", "octocat")]
    [InlineData("alice@example.com", "alice")]
    [InlineData("jean-marc.prieur@ms.com", "jean-marc")]
    [InlineData("jmprieur_microsoft", "jmprieur")]
    [InlineData("jmprieur_microsoft@northeurope.com", "jmprieur")]
    [InlineData("_microsoft", "_microsoft")]
    [InlineData(null, "")]
    public void ShortAuthor_DerivesFirstNameOrAlias(string? login, string expected)
    {
        ReviewLauncher.ShortAuthor(login).Should().Be(expected);
    }

    [Theory]
    [InlineData("agency-microsoft/playground", "playground")]
    [InlineData("Context/MyRepo", "MyRepo")]
    [InlineData("no-slash-repo", "no-slash-repo")]
    public void ShortRepo_DropsOwnerPrefix(string displayRepo, string expected)
    {
        ReviewLauncher.ShortRepo(displayRepo).Should().Be(expected);
    }
}
