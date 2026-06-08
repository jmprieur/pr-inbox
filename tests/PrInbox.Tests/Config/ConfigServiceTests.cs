using FluentAssertions;
using PrInbox.Core.Config;
using PrInbox.Core.Credentials;

namespace PrInbox.Tests.Config;

/// <summary>
/// Unit tests for <see cref="ConfigService"/>. Each test uses a temp
/// file as its config path; doctor checks are <em>not</em> covered here
/// because they shell out to <c>gh</c>/<c>az</c>.
/// </summary>
public sealed class ConfigServiceTests : IDisposable
{
    private readonly string _path;

    public ConfigServiceTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"pr-inbox-test-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void ConfigFileExists_Is_False_When_File_Missing()
    {
        var svc = new ConfigService(_path);
        svc.ConfigFileExists().Should().BeFalse();
    }

    [Fact]
    public async Task GetAsync_Returns_Empty_Default_When_File_Missing()
    {
        var svc = new ConfigService(_path);
        var cfg = await svc.GetAsync();
        cfg.Sources.Should().BeEmpty();
        cfg.Ado.Projects.Should().BeEmpty();
        cfg.IgnoredRepos.Should().BeEmpty();
    }

    [Fact]
    public async Task AddGitHubSourceAsync_Github_Com_Uses_Default_Id()
    {
        var svc = new ConfigService(_path);

        var added = await svc.AddGitHubSourceAsync(SourceConfigKind.GitHub, "github.com");

        added.Should().BeTrue();
        svc.ConfigFileExists().Should().BeTrue();
        var cfg = await svc.GetAsync();
        cfg.Sources.Should().HaveCount(1);
        cfg.Sources[0].Id.Should().Be("gh.com");
        cfg.Sources[0].Kind.Should().Be(SourceConfigKind.GitHub);
        cfg.Sources[0].Host.Should().Be("github.com");
        cfg.Sources[0].Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task AddGitHubSourceAsync_Ghe_Uses_Default_Id_With_Host()
    {
        var svc = new ConfigService(_path);

        await svc.AddGitHubSourceAsync(SourceConfigKind.GitHubEnterprise, "github.contoso.com");

        var cfg = await svc.GetAsync();
        cfg.Sources.Should().HaveCount(1);
        cfg.Sources[0].Id.Should().Be("ghe.github.contoso.com");
        cfg.Sources[0].Kind.Should().Be(SourceConfigKind.GitHubEnterprise);
        cfg.Sources[0].Host.Should().Be("github.contoso.com");
    }

    [Fact]
    public async Task AddGitHubSourceAsync_Honors_Custom_Id()
    {
        var svc = new ConfigService(_path);

        await svc.AddGitHubSourceAsync(SourceConfigKind.GitHubEnterprise, "github.contoso.com", id: "ghe.contoso");

        var cfg = await svc.GetAsync();
        cfg.Sources[0].Id.Should().Be("ghe.contoso");
    }

    [Fact]
    public async Task AddGitHubSourceAsync_Duplicate_Returns_False()
    {
        var svc = new ConfigService(_path);
        (await svc.AddGitHubSourceAsync(SourceConfigKind.GitHub, "github.com")).Should().BeTrue();

        var second = await svc.AddGitHubSourceAsync(SourceConfigKind.GitHub, "github.com");

        second.Should().BeFalse();
        (await svc.GetAsync()).Sources.Should().HaveCount(1);
    }

    [Fact]
    public async Task AddGitHubSourceAsync_Rejects_AzureDevOps_Kind()
    {
        var svc = new ConfigService(_path);

        var act = async () => await svc.AddGitHubSourceAsync(SourceConfigKind.AzureDevOps, "anything");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task AddGitHubSourceAsync_Rejects_Empty_Host()
    {
        var svc = new ConfigService(_path);

        var act = async () => await svc.AddGitHubSourceAsync(SourceConfigKind.GitHub, "   ");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task AddAdoProjectAsync_Adds_Entry()
    {
        var svc = new ConfigService(_path);

        var added = await svc.AddAdoProjectAsync("mseng", "Context");

        added.Should().BeTrue();
        var cfg = await svc.GetAsync();
        cfg.Ado.Projects.Should().HaveCount(1);
        cfg.Ado.Projects[0].Org.Should().Be("mseng");
        cfg.Ado.Projects[0].Project.Should().Be("Context");
    }

    [Fact]
    public async Task AddAdoProjectAsync_Duplicate_Returns_False_CaseInsensitive()
    {
        var svc = new ConfigService(_path);
        await svc.AddAdoProjectAsync("mseng", "Context");

        var second = await svc.AddAdoProjectAsync("MSENG", "context");

        second.Should().BeFalse();
        (await svc.GetAsync()).Ado.Projects.Should().HaveCount(1);
    }

    [Fact]
    public async Task RemoveSourceAsync_Removes_By_Id_CaseInsensitive()
    {
        var svc = new ConfigService(_path);
        await svc.AddGitHubSourceAsync(SourceConfigKind.GitHub, "github.com");

        var removed = await svc.RemoveSourceAsync("GH.COM");

        removed.Should().BeTrue();
        (await svc.GetAsync()).Sources.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveSourceAsync_Returns_False_When_Missing()
    {
        var svc = new ConfigService(_path);
        var removed = await svc.RemoveSourceAsync("does-not-exist");
        removed.Should().BeFalse();
    }

    [Fact]
    public async Task RemoveAdoProjectAsync_Removes_Entry()
    {
        var svc = new ConfigService(_path);
        await svc.AddAdoProjectAsync("mseng", "Context");

        var removed = await svc.RemoveAdoProjectAsync("mseng", "Context");

        removed.Should().BeTrue();
        (await svc.GetAsync()).Ado.Projects.Should().BeEmpty();
    }

    [Fact]
    public async Task SetIgnoredReposAsync_Replaces_List_And_Trims_Empty()
    {
        var svc = new ConfigService(_path);

        await svc.SetIgnoredReposAsync(new[] { "1ES/Spmi", "  ", "", "  contoso/foo  " });

        var cfg = await svc.GetAsync();
        cfg.IgnoredRepos.Should().BeEquivalentTo(new[] { "1ES/Spmi", "contoso/foo" });
    }

    [Fact]
    public async Task SetRepoPathFiltersAsync_Persists_Trims_And_Drops_Empty_Repos()
    {
        var svc = new ConfigService(_path);

        await svc.SetRepoPathFiltersAsync(new Dictionary<string, IReadOnlyList<string>>
        {
            ["contoso/mono"] = new List<string> { "src/A", "  src/B  ", "", "  " },
            ["contoso/blank"] = new List<string> { "  ", "" },
            ["  "] = new List<string> { "src/X" },
        });

        var cfg = await svc.GetAsync();
        cfg.RepoPathFilters.Should().ContainKey("contoso/mono");
        cfg.RepoPathFilters["contoso/mono"].Should().BeEquivalentTo(new[] { "src/A", "src/B" });
        cfg.RepoPathFilters.Should().NotContainKey("contoso/blank");
        cfg.RepoPathFilters.Keys.Should().NotContain(k => string.IsNullOrWhiteSpace(k));
    }

    [Fact]
    public async Task SetRepoPathFiltersAsync_Refreshes_Singleton_InPlace()
    {
        var singleton = new PrInboxConfig();
        var svc = new ConfigService(singleton, _path);

        await svc.SetRepoPathFiltersAsync(new Dictionary<string, IReadOnlyList<string>>
        {
            ["contoso/mono"] = new List<string> { "src/A" },
        });

        singleton.RepoPathFilters.Should().ContainKey("contoso/mono");
        singleton.RepoPathFilters["contoso/mono"].Should().BeEquivalentTo(new[] { "src/A" });

        // A subsequent save replaces the prior content in place.
        await svc.SetRepoPathFiltersAsync(new Dictionary<string, IReadOnlyList<string>>
        {
            ["contoso/other"] = new List<string> { "lib/C" },
        });

        singleton.RepoPathFilters.Should().NotContainKey("contoso/mono");
        singleton.RepoPathFilters.Should().ContainKey("contoso/other");
    }

    [Fact]
    public async Task Mutations_Refresh_Singleton_Lists_InPlace()
    {
        var singleton = new PrInboxConfig();
        var svc = new ConfigService(singleton, _path);

        await svc.AddGitHubSourceAsync(SourceConfigKind.GitHub, "github.com");
        await svc.AddAdoProjectAsync("mseng", "Context");
        await svc.SetIgnoredReposAsync(new[] { "skip/me" });

        singleton.Sources.Should().HaveCount(1);
        singleton.Sources[0].Id.Should().Be("gh.com");
        singleton.Ado.Projects.Should().ContainSingle(p => p.Org == "mseng" && p.Project == "Context");
        singleton.IgnoredRepos.Should().ContainSingle(s => s == "skip/me");
    }

    [Fact]
    public async Task Remove_Refreshes_Singleton()
    {
        var singleton = new PrInboxConfig();
        var svc = new ConfigService(singleton, _path);
        await svc.AddGitHubSourceAsync(SourceConfigKind.GitHub, "github.com");
        singleton.Sources.Should().HaveCount(1);

        await svc.RemoveSourceAsync("gh.com");

        singleton.Sources.Should().BeEmpty();
    }

    [Fact]
    public async Task SetReviewLauncherFlagsAsync_Persists_And_Mirrors_Singleton()
    {
        var singleton = new PrInboxConfig();
        var svc = new ConfigService(singleton, _path);
        singleton.ReviewLauncher.AutoSend.Should().BeTrue();   // baseline default
        singleton.ReviewLauncher.Yolo.Should().BeFalse();

        await svc.SetReviewLauncherFlagsAsync(autoSend: false, yolo: true);

        // Singleton mirrored in-place (same instance — ReviewLauncher reads this).
        singleton.ReviewLauncher.AutoSend.Should().BeFalse();
        singleton.ReviewLauncher.Yolo.Should().BeTrue();

        // And persisted to disk.
        var reloaded = await svc.GetAsync();
        reloaded.ReviewLauncher.AutoSend.Should().BeFalse();
        reloaded.ReviewLauncher.Yolo.Should().BeTrue();

        // Round-trip toggling back also works.
        await svc.SetReviewLauncherFlagsAsync(autoSend: true, yolo: false);
        singleton.ReviewLauncher.AutoSend.Should().BeTrue();
        singleton.ReviewLauncher.Yolo.Should().BeFalse();
    }

    [Fact]
    public async Task SetReviewLauncherTabColorAsync_Normalizes_Persists_And_Mirrors_Singleton()
    {
        var singleton = new PrInboxConfig();
        var svc = new ConfigService(singleton, _path);
        singleton.ReviewLauncher.TabColor.Should().Be("#5da4ff"); // baseline default

        // Valid hex is stored as-is and mirrored onto the singleton.
        await svc.SetReviewLauncherTabColorAsync("#ff8800");
        singleton.ReviewLauncher.TabColor.Should().Be("#ff8800");
        (await svc.GetAsync()).ReviewLauncher.TabColor.Should().Be("#ff8800");

        // Blank disables colouring (stored as empty).
        await svc.SetReviewLauncherTabColorAsync("   ");
        singleton.ReviewLauncher.TabColor.Should().BeEmpty();
        (await svc.GetAsync()).ReviewLauncher.TabColor.Should().BeEmpty();

        // Garbage is rejected to empty rather than reaching wt.
        await svc.SetReviewLauncherTabColorAsync("not-a-colour");
        singleton.ReviewLauncher.TabColor.Should().BeEmpty();
    }

    [Theory]
    [InlineData("#5da4ff", "#5da4ff")]
    [InlineData("  #ABC  ", "#ABC")]
    [InlineData("#FFFFFF", "#FFFFFF")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("5da4ff", null)]      // missing leading '#'
    [InlineData("#12", null)]          // wrong length
    [InlineData("#1234", null)]        // wrong length
    [InlineData("#gggggg", null)]      // non-hex
    public void NormalizeTabColor_AcceptsHexRejectsRest(string input, string? expected)
    {
        ReviewLauncherSettings.NormalizeTabColor(input).Should().Be(expected);
    }

    [Fact]
    public void ConfigPath_Reflects_Constructor_Arg()
    {
        var svc = new ConfigService(_path);
        svc.ConfigPath.Should().Be(_path);
    }

    [Fact]
    public async Task AddGitHubSourceWithIdentityAsync_Github_Com_Uses_Default_Id_With_Suffix()
    {
        var svc = new ConfigService(_path);

        var added = await svc.AddGitHubSourceWithIdentityAsync(SourceConfigKind.GitHub, "github.com", "jmprieur_microsoft");

        added.Should().BeTrue();
        var cfg = await svc.GetAsync();
        cfg.Sources.Should().HaveCount(1);
        cfg.Sources[0].Id.Should().Be("gh.com:jmprieur_microsoft");
        cfg.Sources[0].Kind.Should().Be(SourceConfigKind.GitHub);
        cfg.Sources[0].Host.Should().Be("github.com");
        cfg.Sources[0].Identity.Should().Be("jmprieur_microsoft");
        cfg.Sources[0].Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task AddGitHubSourceWithIdentityAsync_Two_Identities_Same_Host_Both_Persist()
    {
        // Jenny / Jean-Marc scenario: two distinct GitHub logins on the
        // same host should coexist as two sources.
        var svc = new ConfigService(_path);

        await svc.AddGitHubSourceWithIdentityAsync(SourceConfigKind.GitHub, "github.com", "jmprieur");
        await svc.AddGitHubSourceWithIdentityAsync(SourceConfigKind.GitHub, "github.com", "jmprieur_microsoft");

        var cfg = await svc.GetAsync();
        cfg.Sources.Should().HaveCount(2);
        cfg.Sources.Select(s => s.Id).Should().BeEquivalentTo(new[] { "gh.com:jmprieur", "gh.com:jmprieur_microsoft" });
        cfg.Sources.Select(s => s.Identity).Should().BeEquivalentTo(new[] { "jmprieur", "jmprieur_microsoft" });
    }

    [Fact]
    public async Task AddGitHubSourceWithIdentityAsync_Ghe_Uses_Prefixed_Id()
    {
        var svc = new ConfigService(_path);

        await svc.AddGitHubSourceWithIdentityAsync(SourceConfigKind.GitHubEnterprise, "microsoft.ghe.com", "jean-marc-prieur");

        var cfg = await svc.GetAsync();
        cfg.Sources[0].Id.Should().Be("ghe.microsoft.ghe.com:jean-marc-prieur");
        cfg.Sources[0].Host.Should().Be("microsoft.ghe.com");
        cfg.Sources[0].Identity.Should().Be("jean-marc-prieur");
    }

    [Fact]
    public async Task AddGitHubSourceWithIdentityAsync_Honors_Custom_Id()
    {
        var svc = new ConfigService(_path);

        await svc.AddGitHubSourceWithIdentityAsync(SourceConfigKind.GitHub, "github.com", "jmprieur", id: "gh.com:public");

        var cfg = await svc.GetAsync();
        cfg.Sources[0].Id.Should().Be("gh.com:public");
        cfg.Sources[0].Identity.Should().Be("jmprieur");
    }

    [Fact]
    public async Task AddGitHubSourceWithIdentityAsync_Duplicate_Returns_False()
    {
        var svc = new ConfigService(_path);
        await svc.AddGitHubSourceWithIdentityAsync(SourceConfigKind.GitHub, "github.com", "jmprieur");

        var second = await svc.AddGitHubSourceWithIdentityAsync(SourceConfigKind.GitHub, "github.com", "jmprieur");

        second.Should().BeFalse();
        (await svc.GetAsync()).Sources.Should().HaveCount(1);
    }

    [Fact]
    public async Task AddGitHubSourceWithIdentityAsync_Coexists_With_Default_Identity_Source()
    {
        // A default-identity source for github.com plus an explicit one
        // is a supported (though discouraged) configuration.
        var svc = new ConfigService(_path);
        await svc.AddGitHubSourceAsync(SourceConfigKind.GitHub, "github.com");

        await svc.AddGitHubSourceWithIdentityAsync(SourceConfigKind.GitHub, "github.com", "jmprieur");

        var cfg = await svc.GetAsync();
        cfg.Sources.Select(s => s.Id).Should().BeEquivalentTo(new[] { "gh.com", "gh.com:jmprieur" });
    }

    [Fact]
    public async Task AddGitHubSourceWithIdentityAsync_Rejects_Default_Identity()
    {
        var svc = new ConfigService(_path);

        var act = async () => await svc.AddGitHubSourceWithIdentityAsync(SourceConfigKind.GitHub, "github.com", "default");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task AddGitHubSourceWithIdentityAsync_Rejects_Empty_Identity()
    {
        var svc = new ConfigService(_path);

        var act = async () => await svc.AddGitHubSourceWithIdentityAsync(SourceConfigKind.GitHub, "github.com", "  ");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task AddGitHubSourceWithIdentityAsync_Rejects_AzureDevOps_Kind()
    {
        var svc = new ConfigService(_path);

        var act = async () => await svc.AddGitHubSourceWithIdentityAsync(SourceConfigKind.AzureDevOps, "anything", "someone");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task AddGitHubSourceWithIdentityAsync_Trims_Host_And_Identity()
    {
        var svc = new ConfigService(_path);

        await svc.AddGitHubSourceWithIdentityAsync(SourceConfigKind.GitHub, "  github.com  ", "  jmprieur  ");

        var cfg = await svc.GetAsync();
        cfg.Sources[0].Host.Should().Be("github.com");
        cfg.Sources[0].Identity.Should().Be("jmprieur");
        cfg.Sources[0].Id.Should().Be("gh.com:jmprieur");
    }

    // --- BindGitHubSourceToIdentityAsync -----------------------------

    [Fact]
    public async Task BindGitHubSourceToIdentityAsync_Migrates_Default_To_Explicit()
    {
        var svc = new ConfigService(_path);
        await svc.AddGitHubSourceAsync(SourceConfigKind.GitHub, "github.com", id: "gh.com");

        var result = await svc.BindGitHubSourceToIdentityAsync("gh.com", "jmprieur_microsoft");

        result.Should().Be(BindIdentityResult.Migrated);
        var cfg = await svc.GetAsync();
        cfg.Sources.Should().ContainSingle();
        cfg.Sources[0].Id.Should().Be("gh.com:jmprieur_microsoft");
        cfg.Sources[0].Identity.Should().Be("jmprieur_microsoft");
        cfg.Sources[0].Host.Should().Be("github.com");
        cfg.Sources[0].Kind.Should().Be(SourceConfigKind.GitHub);
        cfg.Sources[0].Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task BindGitHubSourceToIdentityAsync_When_Explicit_Already_Exists_Removes_Duplicate()
    {
        var svc = new ConfigService(_path);
        await svc.AddGitHubSourceAsync(SourceConfigKind.GitHub, "github.com", id: "gh.com");
        await svc.AddGitHubSourceWithIdentityAsync(SourceConfigKind.GitHub, "github.com", "jmprieur_microsoft");

        var result = await svc.BindGitHubSourceToIdentityAsync("gh.com", "jmprieur_microsoft");

        result.Should().Be(BindIdentityResult.RemovedDuplicate);
        var cfg = await svc.GetAsync();
        cfg.Sources.Should().ContainSingle();
        cfg.Sources[0].Id.Should().Be("gh.com:jmprieur_microsoft");
    }

    [Fact]
    public async Task BindGitHubSourceToIdentityAsync_Unknown_Id_Returns_NotFound()
    {
        var svc = new ConfigService(_path);
        await svc.AddGitHubSourceAsync(SourceConfigKind.GitHub, "github.com", id: "gh.com");

        var result = await svc.BindGitHubSourceToIdentityAsync("does-not-exist", "jmprieur");

        result.Should().Be(BindIdentityResult.NotFound);
        var cfg = await svc.GetAsync();
        cfg.Sources.Should().ContainSingle().Which.Id.Should().Be("gh.com");
    }

    [Fact]
    public async Task BindGitHubSourceToIdentityAsync_Not_Default_Identity_Returns_NotEligible()
    {
        var svc = new ConfigService(_path);
        await svc.AddGitHubSourceWithIdentityAsync(SourceConfigKind.GitHub, "github.com", "jmprieur", id: "gh.com:jmprieur");

        var result = await svc.BindGitHubSourceToIdentityAsync("gh.com:jmprieur", "jmprieur_microsoft");

        result.Should().Be(BindIdentityResult.NotEligible);
        var cfg = await svc.GetAsync();
        cfg.Sources.Should().ContainSingle().Which.Identity.Should().Be("jmprieur");
    }

    [Fact]
    public async Task BindGitHubSourceToIdentityAsync_Rejects_Default_Target()
    {
        var svc = new ConfigService(_path);
        await svc.AddGitHubSourceAsync(SourceConfigKind.GitHub, "github.com", id: "gh.com");

        var act = async () => await svc.BindGitHubSourceToIdentityAsync("gh.com", "default");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task BindGitHubSourceToIdentityAsync_Works_For_Ghe()
    {
        var svc = new ConfigService(_path);
        await svc.AddGitHubSourceAsync(SourceConfigKind.GitHubEnterprise, "microsoft.ghe.com", id: "ghe.microsoft.ghe.com");

        var result = await svc.BindGitHubSourceToIdentityAsync("ghe.microsoft.ghe.com", "jean-marc");

        result.Should().Be(BindIdentityResult.Migrated);
        var cfg = await svc.GetAsync();
        cfg.Sources.Should().ContainSingle();
        cfg.Sources[0].Kind.Should().Be(SourceConfigKind.GitHubEnterprise);
        cfg.Sources[0].Host.Should().Be("microsoft.ghe.com");
        cfg.Sources[0].Identity.Should().Be("jean-marc");
    }
}
