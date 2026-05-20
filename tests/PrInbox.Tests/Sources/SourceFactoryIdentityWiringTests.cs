using FluentAssertions;
using PrInbox.Core.Credentials;
using PrInbox.Core.Models;
using PrInbox.Sources;

namespace PrInbox.Tests.Sources;

/// <summary>
/// Locks in the multi-identity plumbing across <see cref="SourceFactory"/>:
/// a <see cref="SourceConfig"/> with an explicit <see cref="SourceConfig.Identity"/>
/// must produce a <see cref="RuntimeSource"/> whose token provider passes
/// <c>--user &lt;identity&gt;</c> to <c>gh</c>, and whose <see cref="RuntimeSource.Identity"/>
/// flows through to the storage layer as <c>identity_used</c>.
/// </summary>
public sealed class SourceFactoryIdentityWiringTests
{
    [Fact]
    public void Build_GitHub_Source_With_Explicit_Identity_Plumbs_Through_To_TokenProvider_And_RuntimeSource()
    {
        var config = new PrInboxConfig();
        config.Sources.Add(new SourceConfig
        {
            Id = "gh.com:jmprieur_microsoft",
            Kind = SourceConfigKind.GitHub,
            Host = "github.com",
            Identity = "jmprieur_microsoft",
            Enabled = true,
        });

        var sources = new SourceFactory().Build(config);

        sources.Should().ContainSingle();
        var rt = sources[0];
        rt.Identity.Should().Be("jmprieur_microsoft");
        rt.TokenProvider.Should().BeOfType<GhCliTokenProvider>();
        ((GhCliTokenProvider)rt.TokenProvider).Identity.Should().Be("jmprieur_microsoft");
        ((GhCliTokenProvider)rt.TokenProvider).SourceId.Should().Be("gh.com:jmprieur_microsoft");
    }

    [Fact]
    public void Build_GitHub_Source_With_Default_Identity_Yields_Null_TokenProvider_Identity()
    {
        // "default" is the placeholder for "use whichever gh account is
        // currently active" — GhCliTokenProvider normalises it to null so
        // we skip the --user arg on the shell-out.
        var config = new PrInboxConfig();
        config.Sources.Add(new SourceConfig
        {
            Id = "gh.com",
            Kind = SourceConfigKind.GitHub,
            Host = "github.com",
            Identity = "default",
            Enabled = true,
        });

        var sources = new SourceFactory().Build(config);

        sources.Should().ContainSingle();
        ((GhCliTokenProvider)sources[0].TokenProvider).Identity.Should().BeNull();
        // RuntimeSource still carries the raw "default" string — it ends
        // up as identity_used in the DB row; only the token provider
        // normalises away from "default".
        sources[0].Identity.Should().Be("default");
    }

    [Fact]
    public void Build_Two_GitHub_Sources_Same_Host_Different_Identities_Produces_Two_RuntimeSources()
    {
        // Jenny / Jean-Marc scenario: two identities for github.com must
        // produce two independent runtime sources, each with its own
        // user-scoped token provider.
        var config = new PrInboxConfig();
        config.Sources.Add(new SourceConfig
        {
            Id = "gh.com:jmprieur",
            Kind = SourceConfigKind.GitHub,
            Host = "github.com",
            Identity = "jmprieur",
            Enabled = true,
        });
        config.Sources.Add(new SourceConfig
        {
            Id = "gh.com:jmprieur_microsoft",
            Kind = SourceConfigKind.GitHub,
            Host = "github.com",
            Identity = "jmprieur_microsoft",
            Enabled = true,
        });

        var sources = new SourceFactory().Build(config);

        sources.Should().HaveCount(2);
        sources.Select(s => s.Identity).Should().BeEquivalentTo(new[] { "jmprieur", "jmprieur_microsoft" });
        sources.Select(s => ((GhCliTokenProvider)s.TokenProvider).Identity)
            .Should().BeEquivalentTo(new[] { "jmprieur", "jmprieur_microsoft" });
        // Token providers must be distinct instances — each one is bound
        // to its source's identity and must not be shared across sources.
        sources[0].TokenProvider.Should().NotBeSameAs(sources[1].TokenProvider);
    }

    [Fact]
    public void Build_Disabled_Source_Is_Skipped()
    {
        var config = new PrInboxConfig();
        config.Sources.Add(new SourceConfig
        {
            Id = "gh.com:disabled",
            Kind = SourceConfigKind.GitHub,
            Host = "github.com",
            Identity = "disabled_login",
            Enabled = false,
        });

        var sources = new SourceFactory().Build(config);

        sources.Should().BeEmpty();
    }

    [Fact]
    public void Build_Ghe_Source_With_Identity_Plumbs_Through()
    {
        var config = new PrInboxConfig();
        config.Sources.Add(new SourceConfig
        {
            Id = "ghe.microsoft.ghe.com:jean-marc-prieur",
            Kind = SourceConfigKind.GitHubEnterprise,
            Host = "microsoft.ghe.com",
            Identity = "jean-marc-prieur",
            Enabled = true,
        });

        var sources = new SourceFactory().Build(config);

        sources.Should().ContainSingle();
        sources[0].Identity.Should().Be("jean-marc-prieur");
        ((GhCliTokenProvider)sources[0].TokenProvider).Identity.Should().Be("jean-marc-prieur");
    }
}
