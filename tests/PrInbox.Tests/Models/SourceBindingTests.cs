using PrInbox.Core.Models;

namespace PrInbox.Tests.Models;

public class SourceBindingTests
{
    [Fact]
    public void SourceId_Combines_Host_And_Identity_For_GitHub()
    {
        var sb = new SourceBinding(SourceKind.GitHub, "github.com", "jmprieur_microsoft");
        sb.SourceId.Should().Be("github.com:jmprieur_microsoft");
    }

    [Fact]
    public void SourceId_Combines_Host_And_Identity_For_Ghe()
    {
        var sb = new SourceBinding(SourceKind.GitHubEnterprise, "ghe.example.com", "jean-marc-prieur");
        sb.SourceId.Should().Be("ghe.example.com:jean-marc-prieur");
    }

    [Fact]
    public void SourceId_Uses_Org_As_Identity_For_Ado()
    {
        var sb = new SourceBinding(SourceKind.AzureDevOps, "dev.azure.com", "fabrikam");
        sb.SourceId.Should().Be("dev.azure.com:fabrikam");
    }

    [Fact]
    public void ToString_Returns_SourceId()
    {
        var sb = new SourceBinding(SourceKind.GitHub, "github.com", "jmprieur");
        sb.ToString().Should().Be("github.com:jmprieur");
    }

    [Fact]
    public void Equality_Is_Structural()
    {
        var a = new SourceBinding(SourceKind.GitHub, "github.com", "jmprieur");
        var b = new SourceBinding(SourceKind.GitHub, "github.com", "jmprieur");
        var c = new SourceBinding(SourceKind.GitHub, "github.com", "jmprieur_microsoft");
        a.Should().Be(b);
        a.Should().NotBe(c);
    }
}
