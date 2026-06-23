using PrInbox.Core.Credentials;

namespace PrInbox.Tests.Credentials;

public class ConfigProfileTests
{
    [Fact]
    public void ApplyTo_OverlaysClassesAndLaunchCommand()
    {
        var config = new PrInboxConfig();
        config.IdentityClasses.Should().ContainSingle(c => c.Name == "Public"); // shipped default

        var profile = new ConfigProfile
        {
            IdentityClasses = new()
            {
                new IdentityClass { Name = "EMU", Host = "github.com", AliasSuffix = "_microsoft" },
                new IdentityClass { Name = "Public", Host = "github.com", AliasSuffix = "" },
            },
            ReviewLauncher = new ReviewLauncherProfile
            {
                LaunchCommand = "agency copilot --plugin {plugin} --model {model} --agent {agent}",
                Model = "claude-opus-4.8",
            },
        };

        var changes = profile.ApplyTo(config);

        changes.Should().HaveCount(3);
        config.IdentityClasses.Should().HaveCount(2);
        config.IdentityClasses[0].Name.Should().Be("EMU");
        config.ReviewLauncher.LaunchCommand
            .Should().Be("agency copilot --plugin {plugin} --model {model} --agent {agent}");
        config.ReviewLauncher.Model.Should().Be("claude-opus-4.8");
    }

    [Fact]
    public void ApplyTo_EmptyProfile_MakesNoChanges()
    {
        var config = new PrInboxConfig();
        new ConfigProfile().ApplyTo(config).Should().BeEmpty();
        config.IdentityClasses.Should().ContainSingle(c => c.Name == "Public");
    }

    [Fact]
    public void Deserialize_MicrosoftProfileShape()
    {
        const string json = """
        {
          "identityClasses": [
            { "name": "EMU", "host": "github.com", "aliasSuffix": "_microsoft" }
          ],
          "reviewLauncher": { "launchCommand": "agency copilot" }
        }
        """;

        var profile = System.Text.Json.JsonSerializer.Deserialize<ConfigProfile>(json, ConfigProfile.JsonOptions);

        profile.Should().NotBeNull();
        profile!.IdentityClasses.Should().ContainSingle(c => c.Name == "EMU" && c.AliasSuffix == "_microsoft");
        profile.ReviewLauncher!.LaunchCommand.Should().Be("agency copilot");
    }
}
