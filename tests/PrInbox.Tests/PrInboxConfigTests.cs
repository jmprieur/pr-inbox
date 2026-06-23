using PrInbox.Core.Credentials;

namespace PrInbox.Tests;

public class PrInboxConfigTests
{
    [Fact]
    public async Task LoadAsync_From_Missing_Path_Returns_Empty_Default()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pr-inbox-{Guid.NewGuid():N}.json");
        var config = await PrInboxConfig.LoadAsync(path);

        config.Should().NotBeNull();
        config.SchemaVersion.Should().Be(1);
        config.Sources.Should().BeEmpty();
        config.Ado.Projects.Should().BeEmpty();
        config.Bots.ExtraLogins.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_Then_LoadAsync_Round_Trips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pr-inbox-{Guid.NewGuid():N}.json");
        try
        {
            var original = new PrInboxConfig
            {
                Sources =
                {
                    new SourceConfig
                    {
                        Id = "gh.com",
                        Kind = SourceConfigKind.GitHub,
                        Host = "github.com",
                        Identity = "default",
                        Enabled = true,
                    },
                    new SourceConfig
                    {
                        Id = "ado:fabrikam",
                        Kind = SourceConfigKind.AzureDevOps,
                        Host = null,
                        Identity = "default",
                        Enabled = true,
                    },
                },
                Ado = new AdoConfig
                {
                    Projects = { new AdoProjectConfig { Org = "fabrikam", Project = "Context" } },
                },
                Bots = new BotConfig { ExtraLogins = { "Copilot" } },
            };

            await original.SaveAsync(path);
            File.Exists(path).Should().BeTrue();

            var loaded = await PrInboxConfig.LoadAsync(path);
            loaded.Sources.Should().HaveCount(2);
            loaded.Sources.Should().Contain(s => s.Id == "gh.com" && s.Kind == SourceConfigKind.GitHub);
            loaded.Sources.Should().Contain(s => s.Id == "ado:fabrikam" && s.Kind == SourceConfigKind.AzureDevOps);
            loaded.Ado.Projects.Should().ContainSingle(p => p.Org == "fabrikam" && p.Project == "Context");
            loaded.Bots.ExtraLogins.Should().ContainSingle(l => l == "Copilot");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task DefaultPath_Honors_Environment_Override()
    {
        var prev = Environment.GetEnvironmentVariable("PR_INBOX_CONFIG_PATH");
        try
        {
            var expected = Path.Combine(Path.GetTempPath(), "pr-inbox-override.json");
            Environment.SetEnvironmentVariable("PR_INBOX_CONFIG_PATH", expected);
            PrInboxConfig.DefaultPath.Should().Be(expected);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PR_INBOX_CONFIG_PATH", prev);
        }
    }
}
