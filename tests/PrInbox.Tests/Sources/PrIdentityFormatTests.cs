using PrInbox.Core.Models;

namespace PrInbox.Tests.Sources;

/// <summary>
/// Verifies <see cref="PrIdentity"/> URL helpers produce the canonical
/// PR URL for each source kind, and that the type's structural invariants
/// (validation, equality, ToString) hold.
/// </summary>
public class PrIdentityFormatTests
{
    [Fact]
    public void GitHub_Url_Has_Expected_Shape()
    {
        var url = PrIdentity.FormatGitHubUrl("agency-microsoft", "playground", 4248);
        url.Should().Be("https://github.com/agency-microsoft/playground/pull/4248");
    }

    [Fact]
    public void GitHub_Stable_Uses_Numeric_Ids()
    {
        var stable = PrIdentity.FormatGitHubStable(repoId: 123456L, prId: 987654321L);
        stable.Should().Be("gh.com:123456#987654321");
    }

    [Fact]
    public void Ghe_Url_Includes_Host()
    {
        var url = PrIdentity.FormatGheUrl("github.contoso.com", "foo", "bar", 812);
        url.Should().Be("https://github.contoso.com/foo/bar/pull/812");
    }

    [Fact]
    public void Ghe_Stable_Includes_Host_And_Numeric_Ids()
    {
        var stable = PrIdentity.FormatGheStable("github.contoso.com", repoId: 1L, prId: 2L);
        stable.Should().Be("ghe.github.contoso.com:1#2");
    }

    [Fact]
    public void Ado_Url_Has_Org_Project_Repo_Number()
    {
        var url = PrIdentity.FormatAdoUrl("mseng", "Context", "Private", 1234);
        url.Should().Be("https://dev.azure.com/mseng/Context/_git/Private/pullrequest/1234");
    }

    [Fact]
    public void Ado_Stable_Uses_Guids()
    {
        var project = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var repo = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var stable = PrIdentity.FormatAdoStable("mseng", project, repo, 1234);
        stable.Should().Be("ado:mseng/11111111-1111-1111-1111-111111111111/22222222-2222-2222-2222-222222222222#1234");
    }

    [Fact]
    public void Validate_Rejects_Empty_Url()
    {
        var id = new PrIdentity(Url: "", Stable: "gh.com:1#2");
        var act = id.Validate;
        act.Should().Throw<ArgumentException>().WithParameterName("Url");
    }

    [Fact]
    public void Validate_Rejects_Empty_Stable()
    {
        var id = new PrIdentity(Url: "https://github.com/o/r/pull/1", Stable: "");
        var act = id.Validate;
        act.Should().Throw<ArgumentException>().WithParameterName("Stable");
    }

    [Fact]
    public void Equality_Is_Structural()
    {
        var a = new PrIdentity("https://github.com/o/r/pull/1", "gh.com:1#2");
        var b = new PrIdentity("https://github.com/o/r/pull/1", "gh.com:1#2");
        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void ToString_Returns_Url()
    {
        var id = new PrIdentity("https://github.com/o/r/pull/1", "gh.com:1#2");
        id.ToString().Should().Be("https://github.com/o/r/pull/1");
    }
}
