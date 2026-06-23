using FluentAssertions;
using PrInbox.Core.Credentials;

namespace PrInbox.Tests.Credentials;

/// <summary>
/// Tests for <see cref="GhAuthStatusParser"/>. Feeds canned output
/// snapshots of <c>gh auth status</c> across versions so the multi-
/// identity Settings UX can rely on a stable parse contract without a
/// real <c>gh</c> binary present.
/// </summary>
public sealed class GhAuthStatusParserTests
{
    [Fact]
    public void Parse_Empty_Input_Returns_Empty()
    {
        GhAuthStatusParser.Parse(string.Empty, "github.com").Should().BeEmpty();
        GhAuthStatusParser.Parse("   \n  \n", "github.com").Should().BeEmpty();
    }

    [Fact]
    public void Parse_Single_Account_Old_Style()
    {
        // gh < 2.40 style — single account per host, no "Active account" line.
        const string output = """
            github.com
              ✓ Logged in to github.com as jmprieur (oauth_token)
              ✓ Git operations for github.com configured to use https protocol.
              ✓ Token: gho_*****
            """;

        var ids = GhAuthStatusParser.Parse(output, "github.com");

        ids.Should().HaveCount(1);
        ids[0].Login.Should().Be("jmprieur");
        ids[0].IsActive.Should().BeTrue();
        ids[0].IsEmu.Should().BeFalse();
    }

    [Fact]
    public void Parse_Multi_Account_New_Style_Marks_Active()
    {
        // gh 2.40+ style — multi-account, explicit "Active account" line.
        const string output = """
            github.com
              ✓ Logged in to github.com account jmprieur (keyring)
              - Active account: true
              - Git operations protocol: https
              - Token: gho_*****
              - Token scopes: 'gist', 'read:org', 'repo'

              ✓ Logged in to github.com account jmprieur_microsoft (keyring)
              - Active account: false
              - Git operations protocol: https
              - Token: gho_*****
              - Token scopes: 'admin:public_key', 'gist', 'read:org', 'repo'
            """;

        var ids = GhAuthStatusParser.Parse(output, "github.com");

        ids.Should().HaveCount(2);
        ids.Select(i => i.Login).Should().BeEquivalentTo(new[] { "jmprieur", "jmprieur_microsoft" });
        ids.Single(i => i.Login == "jmprieur").IsActive.Should().BeTrue();
        ids.Single(i => i.Login == "jmprieur_microsoft").IsActive.Should().BeFalse();
        ids.Single(i => i.Login == "jmprieur_microsoft").IsEmu.Should().BeTrue();
        ids.Single(i => i.Login == "jmprieur").IsEmu.Should().BeFalse();
    }

    [Fact]
    public void Parse_Three_Accounts_Jenny_Scenario()
    {
        // Jenny's setup — three identities. Order shouldn't matter; the
        // parser preserves the order in which gh emits them.
        const string output = """
            github.com
              ✓ Logged in to github.com account jenny_personal (keyring)
              - Active account: true

              ✓ Logged in to github.com account jenny_microsoft (keyring)
              - Active account: false

              ✓ Logged in to github.com account jenny_other_org (keyring)
              - Active account: false
            """;

        var ids = GhAuthStatusParser.Parse(output, "github.com");

        ids.Should().HaveCount(3);
        ids.Count(i => i.IsActive).Should().Be(1);
        ids.Where(i => i.IsEmu).Select(i => i.Login)
            .Should().BeEquivalentTo(new[] { "jenny_personal", "jenny_microsoft", "jenny_other_org" });
    }

    [Fact]
    public void Parse_Filters_By_Hostname()
    {
        // gh can emit blocks for multiple hosts in one report; we only
        // want logins for the requested host.
        const string output = """
            github.com
              ✓ Logged in to github.com account jmprieur (keyring)
              - Active account: true

            ghe.example.com
              ✓ Logged in to ghe.example.com account jean-marc-prieur (keyring)
              - Active account: true
            """;

        var gh = GhAuthStatusParser.Parse(output, "github.com");
        var ghe = GhAuthStatusParser.Parse(output, "ghe.example.com");

        gh.Should().ContainSingle(i => i.Login == "jmprieur");
        ghe.Should().ContainSingle(i => i.Login == "jean-marc-prieur");
    }

    [Fact]
    public void Parse_Is_Case_Insensitive_On_Hostname()
    {
        const string output = """
              ✓ Logged in to github.com account jmprieur (keyring)
              - Active account: true
            """;

        var ids = GhAuthStatusParser.Parse(output, "GITHUB.COM");

        ids.Should().ContainSingle(i => i.Login == "jmprieur");
    }

    [Fact]
    public void Parse_Deduplicates_Repeated_Logins()
    {
        // Defensive: if gh somehow lists the same login twice (e.g. mixed
        // output from stdout+stderr both captured), we collapse to one.
        const string output = """
              ✓ Logged in to github.com account jmprieur (keyring)
              - Active account: true
              ✓ Logged in to github.com account jmprieur (keyring)
              - Active account: true
            """;

        var ids = GhAuthStatusParser.Parse(output, "github.com");

        ids.Should().HaveCount(1);
    }

    [Fact]
    public void Parse_Skips_Other_Hosts()
    {
        const string output = """
              ✓ Logged in to gitlab.com account someone (keyring)
              - Active account: true
            """;

        var ids = GhAuthStatusParser.Parse(output, "github.com");

        ids.Should().BeEmpty();
    }

    [Fact]
    public void Parse_Active_Line_Stops_At_Next_Account()
    {
        // Defensive: if a missing "Active account" line falls through to
        // the next account's marker, we must NOT carry the next account's
        // active state back to the previous one.
        const string output = """
              ✓ Logged in to github.com account alpha (keyring)
              - Git operations protocol: https

              ✓ Logged in to github.com account beta (keyring)
              - Active account: true
            """;

        var ids = GhAuthStatusParser.Parse(output, "github.com");

        ids.Should().HaveCount(2);
        ids.Single(i => i.Login == "alpha").IsActive.Should().BeFalse();
        ids.Single(i => i.Login == "beta").IsActive.Should().BeTrue();
    }

    [Fact]
    public void Parse_Extracts_Token_Scopes_Per_Account()
    {
        const string output = """
              ✓ Logged in to github.com account jmprieur (keyring)
              - Active account: true
              - Token: gho_*****
              - Token scopes: 'gist', 'read:org', 'repo'

              ✓ Logged in to github.com account jmprieur_microsoft (keyring)
              - Active account: false
              - Token: gho_*****
              - Token scopes: 'admin:public_key', 'gist', 'read:org', 'repo', 'workflow'
            """;

        var ids = GhAuthStatusParser.Parse(output, "github.com");

        ids.Single(i => i.Login == "jmprieur").Scopes
            .Should().BeEquivalentTo(new[] { "gist", "read:org", "repo" });
        ids.Single(i => i.Login == "jmprieur_microsoft").Scopes
            .Should().BeEquivalentTo(new[] { "admin:public_key", "gist", "read:org", "repo", "workflow" });
    }

    [Fact]
    public void Parse_Missing_Scopes_Line_Yields_Empty_List()
    {
        const string output = """
              ✓ Logged in to github.com account someone (keyring)
              - Active account: true
              - Token: gho_*****
            """;

        var ids = GhAuthStatusParser.Parse(output, "github.com");

        ids.Should().ContainSingle();
        ids[0].Scopes.Should().BeEmpty();
    }
}
