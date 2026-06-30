using System.ComponentModel;
using System.Text.Json;
using PrInbox.Core.Credentials;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PrInbox.Cli.Commands;

internal sealed class ConfigImportSettings : CommandSettings
{
    [CommandArgument(0, "<FILE>")]
    [Description("Path to a profile JSON, e.g. profiles/microsoft.json.")]
    public required string File { get; init; }

    [CommandOption("--config <PATH>")]
    public string? ConfigPath { get; init; }
}

/// <summary>
/// Imports a <see cref="ConfigProfile"/> (identity classes + review launch
/// command) into the user's config. Used by Start-internal.bat to apply the
/// Microsoft profile, but works for any org's profile file.
/// </summary>
internal sealed class ConfigImportCommand : AsyncCommand<ConfigImportSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ConfigImportSettings settings, CancellationToken cancellationToken)
    {
        if (!System.IO.File.Exists(settings.File))
        {
            AnsiConsole.MarkupLine($"[red]Profile not found:[/] {Markup.Escape(settings.File)}");
            return 1;
        }

        ConfigProfile? profile;
        try
        {
            var json = await System.IO.File.ReadAllTextAsync(settings.File);
            profile = JsonSerializer.Deserialize<ConfigProfile>(json, ConfigProfile.JsonOptions);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Could not parse profile:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        if (profile is null)
        {
            AnsiConsole.MarkupLine("[red]Empty or invalid profile.[/]");
            return 1;
        }

        var config = await PrInboxConfig.LoadAsync(settings.ConfigPath);
        var changes = profile.ApplyTo(config);
        if (changes.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Profile contained nothing to import.[/]");
            return 0;
        }

        await config.SaveAsync(settings.ConfigPath);
        AnsiConsole.MarkupLine($"[green]Imported[/] {Markup.Escape(string.Join(", ", changes))} from [cyan]{Markup.Escape(settings.File)}[/].");
        return 0;
    }
}
