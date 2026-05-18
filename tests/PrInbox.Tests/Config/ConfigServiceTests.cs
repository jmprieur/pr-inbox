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
    public void ConfigPath_Reflects_Constructor_Arg()
    {
        var svc = new ConfigService(_path);
        svc.ConfigPath.Should().Be(_path);
    }
}
