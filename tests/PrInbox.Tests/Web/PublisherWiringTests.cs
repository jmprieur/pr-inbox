using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PrInbox.Core.Credentials;
using PrInbox.Publishers;
using PrInbox.Web.Services;

namespace PrInbox.Tests.Web;

public sealed class PublisherWiringTests
{
    [Fact]
    public void BuildSelector_registers_ado_publisher_from_configured_projects()
    {
        var config = new PrInboxConfig();
        config.Ado.Projects.Add(new AdoProjectConfig
        {
            Org = "fabrikam",
            Project = "Context",
        });
        using var http = new HttpClient();

        var selector = PublisherWiring.BuildSelector(
            config,
            NullLoggerFactory.Instance,
            http);

        selector.Select("https://dev.azure.com/fabrikam/Context/_git/Private/pullrequest/100")
            .Should().BeOfType<AdoReviewPublisher>();
        selector.SelectFor(
                "https://dev.azure.com/fabrikam/Context/_git/Private/pullrequest/100",
                "azure-cli")
            .Should().BeOfType<AdoReviewPublisher>();
    }

    [Fact]
    public void BuildSelector_preserves_legacy_ado_default_identity()
    {
        var config = new PrInboxConfig();
        config.Sources.Add(new SourceConfig
        {
            Id = "ado:legacy",
            Kind = SourceConfigKind.AzureDevOps,
            Identity = "legacy-identity",
        });
        config.Ado.Projects.Add(new AdoProjectConfig
        {
            Org = "fabrikam",
            Project = "Context",
        });
        using var http = new HttpClient();

        var selector = PublisherWiring.BuildSelector(
            config,
            NullLoggerFactory.Instance,
            http);

        selector.IdentityForLogging(
                "https://dev.azure.com/fabrikam/Context/_git/Private/pullrequest/100")
            .Should().Be("legacy-identity");
        selector.SelectFor(
                "https://dev.azure.com/fabrikam/Context/_git/Private/pullrequest/100",
                "azure-cli")
            .Should().BeOfType<AdoReviewPublisher>();
    }
}
