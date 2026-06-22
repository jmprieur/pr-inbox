using FluentAssertions;
using Xunit;

namespace PrInbox.Publishers.Tests;

public sealed class PullRequestUrlParserTests
{
    [Fact]
    public void Parses_github_dotcom_url()
    {
        var r = PullRequestUrlParser.Parse("https://github.com/octo-org/plugins/pull/90");
        r.Kind.Should().Be(PrPlatform.GitHubDotCom);
        r.Host.Should().Be("github.com");
        r.Owner.Should().Be("octo-org");
        r.Repo.Should().Be("plugins");
        r.Number.Should().Be(90);
    }

    [Fact]
    public void Parses_github_enterprise_url()
    {
        var r = PullRequestUrlParser.Parse("https://ghe.example.com/octocat/hello-world/pull/585");
        r.Kind.Should().Be(PrPlatform.GitHubEnterprise);
        r.Host.Should().Be("ghe.example.com");
        r.Owner.Should().Be("octocat");
        r.Repo.Should().Be("hello-world");
        r.Number.Should().Be(585);
    }

    [Fact]
    public void Parses_ado_url()
    {
        var r = PullRequestUrlParser.Parse("https://dev.azure.com/fabrikam/Context/_git/Private/pullrequest/922423");
        r.Kind.Should().Be(PrPlatform.AzureDevOps);
        r.Host.Should().Be("dev.azure.com");
        r.Owner.Should().Be("fabrikam");
        r.AdoProject.Should().Be("Context");
        r.Repo.Should().Be("Private");
        r.Number.Should().Be(922423);
    }

    [Fact]
    public void Rejects_malformed_url()
    {
        Action act = () => PullRequestUrlParser.Parse("not a url");
        act.Should().Throw<ArgumentException>();
    }
}
