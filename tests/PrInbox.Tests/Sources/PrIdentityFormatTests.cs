using PrInbox.Core.Models;

namespace PrInbox.Tests.Sources;

/// <summary>
/// Verifies <see cref="PrIdentity"/> formatting helpers produce the
/// expected canonical strings for each source kind.
/// </summary>
public class PrIdentityFormatTests
{
    [Fact]
    public void GitHub_Display_Has_Expected_Shape()
    {
        var display = PrIdentity.FormatGitHubDisplay("agency-microsoft", "playground", 4248);
        display.Should().Be("gh.com:agency-microsoft/playground#4248");
    }

    [Fact]
    public void GitHub_Stable_Uses_Numeric_Ids()
    {
        var stable = PrIdentity.FormatGitHubStable(repoId: 123456L, prId: 987654321L);
        stable.Should().Be("gh.com:123456#987654321");
    }

    [Fact]
    public void Ghe_Display_Includes_Host()
    {
        var display = PrIdentity.FormatGheDisplay("github.contoso.com", "foo", "bar", 812);
        display.Should().Be("ghe.github.contoso.com:foo/bar#812");
    }

    [Fact]
    public void Ghe_Stable_Includes_Host_And_Numeric_Ids()
    {
        var stable = PrIdentity.FormatGheStable("github.contoso.com", repoId: 1L, prId: 2L);
        stable.Should().Be("ghe.github.contoso.com:1#2");
    }

    [Fact]
    public void Ado_Display_Has_Org_Project_Repo_Number()
    {
        var display = PrIdentity.FormatAdoDisplay("mseng", "Context", "Private", 1234);
        display.Should().Be("ado:mseng/Context/Private#1234");
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
    public void Validate_Rejects_Empty_Display()
    {
        var id = new PrIdentity(Display: "", Stable: "gh.com:1#2");
        var act = id.Validate;
        act.Should().Throw<ArgumentException>().WithParameterName("Display");
    }

    [Fact]
    public void Validate_Rejects_Empty_Stable()
    {
        var id = new PrIdentity(Display: "gh.com:o/r#1", Stable: "");
        var act = id.Validate;
        act.Should().Throw<ArgumentException>().WithParameterName("Stable");
    }

    [Fact]
    public void Equality_Is_Structural()
    {
        var a = new PrIdentity("gh.com:o/r#1", "gh.com:1#2");
        var b = new PrIdentity("gh.com:o/r#1", "gh.com:1#2");
        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void ToString_Returns_Display()
    {
        var id = new PrIdentity("gh.com:o/r#1", "gh.com:1#2");
        id.ToString().Should().Be("gh.com:o/r#1");
    }
}
