using FluentAssertions;
using Xunit;

namespace PrInbox.Publishers.Tests;

public sealed class PullRequestUrlParserTests
{
    [Fact]
    public void Parses_github_dotcom_url()
    {
        var r = PullRequestUrlParser.Parse("https://github.com/1ES-microsoft/ai-plugins/pull/90");
        r.Kind.Should().Be(PrPlatform.GitHubDotCom);
        r.Host.Should().Be("github.com");
        r.Owner.Should().Be("1ES-microsoft");
        r.Repo.Should().Be("ai-plugins");
        r.Number.Should().Be(90);
    }

    [Fact]
    public void Parses_github_enterprise_url()
    {
        var r = PullRequestUrlParser.Parse("https://microsoft.ghe.com/bic/IOM-Libs/pull/585");
        r.Kind.Should().Be(PrPlatform.GitHubEnterprise);
        r.Host.Should().Be("microsoft.ghe.com");
        r.Owner.Should().Be("bic");
        r.Repo.Should().Be("IOM-Libs");
        r.Number.Should().Be(585);
    }

    [Fact]
    public void Parses_ado_url()
    {
        var r = PullRequestUrlParser.Parse("https://dev.azure.com/mseng/Context/_git/Private/pullrequest/922423");
        r.Kind.Should().Be(PrPlatform.AzureDevOps);
        r.Host.Should().Be("dev.azure.com");
        r.Owner.Should().Be("mseng");
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
