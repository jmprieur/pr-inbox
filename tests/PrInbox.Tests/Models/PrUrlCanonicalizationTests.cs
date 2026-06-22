using PrInbox.Core.Models;

namespace PrInbox.Tests.Models;

/// <summary>
/// Canonicalization and parsing tests for <see cref="PrUrl"/>.
/// </summary>
public class PrUrlCanonicalizationTests
{
    [Theory]
    [InlineData(
        "https://github.com/owner/repo/pull/42",
        "https://github.com/owner/repo/pull/42")]
    [InlineData(
        "HTTPS://GITHUB.COM/Owner/Repo/PULL/42",
        "https://github.com/Owner/Repo/pull/42")]
    [InlineData(
        "https://github.com/owner/repo/pull/42/",
        "https://github.com/owner/repo/pull/42")]
    [InlineData(
        "https://github.com/owner/repo/pull/42?diff=split",
        "https://github.com/owner/repo/pull/42")]
    [InlineData(
        "https://github.com/owner/repo/pull/42#issuecomment-1",
        "https://github.com/owner/repo/pull/42")]
    [InlineData(
        "http://github.com/owner/repo/pull/42",
        "https://github.com/owner/repo/pull/42")]
    public void Canonicalize_Normalizes_GitHub_Urls(string input, string expected)
    {
        PrUrl.Canonicalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(
        "https://ghe.example.com/Azure/azure-sdk-for-net/pull/12345",
        "https://ghe.example.com/Azure/azure-sdk-for-net/pull/12345")]
    [InlineData(
        "https://GHE.EXAMPLE.COM/Azure/azure-sdk-for-net/pull/12345/",
        "https://ghe.example.com/Azure/azure-sdk-for-net/pull/12345")]
    public void Canonicalize_Normalizes_Ghe_Urls(string input, string expected)
    {
        PrUrl.Canonicalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(
        "https://dev.azure.com/fabrikam/Context/_git/Private/pullrequest/1234",
        "https://dev.azure.com/fabrikam/Context/_git/Private/pullrequest/1234")]
    [InlineData(
        "https://contoso.visualstudio.com/MyProject/_git/MyRepo/pullrequest/42",
        "https://dev.azure.com/contoso/MyProject/_git/MyRepo/pullrequest/42")]
    [InlineData(
        "https://dev.azure.com/fabrikam/Context/_git/Private/pullrequest/1234/",
        "https://dev.azure.com/fabrikam/Context/_git/Private/pullrequest/1234")]
    public void Canonicalize_Normalizes_Ado_Urls(string input, string expected)
    {
        PrUrl.Canonicalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://github.com/owner/repo/pull/42")]
    [InlineData("https://example.com/")]
    [InlineData("https://github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo/pull/abc")]
    [InlineData("")]
    [InlineData("   ")]
    public void Canonicalize_Throws_On_Invalid_Url(string input)
    {
        var act = () => PrUrl.Canonicalize(input);
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void TryCanonicalize_Returns_True_For_Valid_Url()
    {
        var ok = PrUrl.TryCanonicalize("https://github.com/o/r/pull/1", out var canonical);
        ok.Should().BeTrue();
        canonical.Should().Be("https://github.com/o/r/pull/1");
    }

    [Fact]
    public void TryCanonicalize_Returns_False_For_Invalid_Url()
    {
        var ok = PrUrl.TryCanonicalize("garbage", out var canonical);
        ok.Should().BeFalse();
        canonical.Should().BeEmpty();
    }

    [Fact]
    public void Parse_Returns_Components_For_GitHub_Url()
    {
        var parsed = PrUrl.Parse("https://github.com/octocat/playground/pull/4248");
        parsed.Platform.Should().Be(PrPlatform.GitHub);
        parsed.Host.Should().Be("github.com");
        parsed.Owner.Should().Be("octocat");
        parsed.Project.Should().BeNull();
        parsed.Repo.Should().Be("playground");
        parsed.Number.Should().Be(4248);
        parsed.Canonical.Should().Be("https://github.com/octocat/playground/pull/4248");
    }

    [Fact]
    public void Parse_Returns_Components_For_Ghe_Url()
    {
        var parsed = PrUrl.Parse("https://ghe.example.com/Azure/azure-sdk-for-net/pull/12345");
        parsed.Platform.Should().Be(PrPlatform.GitHubEnterprise);
        parsed.Host.Should().Be("ghe.example.com");
        parsed.Owner.Should().Be("Azure");
        parsed.Repo.Should().Be("azure-sdk-for-net");
        parsed.Number.Should().Be(12345);
    }

    [Fact]
    public void Parse_Returns_Components_For_Ado_Url()
    {
        var parsed = PrUrl.Parse("https://dev.azure.com/fabrikam/Context/_git/Private/pullrequest/1234");
        parsed.Platform.Should().Be(PrPlatform.AzureDevOps);
        parsed.Host.Should().Be("dev.azure.com");
        parsed.Owner.Should().Be("fabrikam");
        parsed.Project.Should().Be("Context");
        parsed.Repo.Should().Be("Private");
        parsed.Number.Should().Be(1234);
    }

    [Fact]
    public void Parse_Returns_Components_For_Legacy_Ado_Url()
    {
        var parsed = PrUrl.Parse("https://contoso.visualstudio.com/MyProject/_git/MyRepo/pullrequest/42");
        parsed.Platform.Should().Be(PrPlatform.AzureDevOps);
        parsed.Host.Should().Be("dev.azure.com");
        parsed.Owner.Should().Be("contoso");
        parsed.Project.Should().Be("MyProject");
        parsed.Repo.Should().Be("MyRepo");
        parsed.Number.Should().Be(42);
        parsed.Canonical.Should().Be("https://dev.azure.com/contoso/MyProject/_git/MyRepo/pullrequest/42");
    }

    [Fact]
    public void Canonicalize_Is_Idempotent()
    {
        var first = PrUrl.Canonicalize("HTTPS://GITHUB.COM/Owner/Repo/pull/42/?diff=split");
        var second = PrUrl.Canonicalize(first);
        second.Should().Be(first);
    }
}
