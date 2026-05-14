using System.ComponentModel;
using PrInbox.Core.Credentials;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PrInbox.Cli.Commands;

internal sealed class ConfigDoctorSettings : CommandSettings
{
    [CommandOption("--config <PATH>")]
    public string? ConfigPath { get; init; }
}

internal sealed class ConfigDoctorCommand : AsyncCommand<ConfigDoctorSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ConfigDoctorSettings settings)
    {
        var config = await PrInboxConfig.LoadAsync(settings.ConfigPath);

        AnsiConsole.MarkupLine("[bold]pr-inbox config doctor[/]");
        AnsiConsole.MarkupLine($"  config: [cyan]{Markup.Escape(settings.ConfigPath ?? PrInboxConfig.DefaultPath)}[/]");
        AnsiConsole.WriteLine();

        if (config.Sources.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No sources configured.[/]");
            AnsiConsole.MarkupLine("Run [bold]pr-inbox config init[/] then add sources.");
            return 1;
        }

        var allOk = true;
        foreach (var sc in config.Sources)
        {
            AnsiConsole.Markup($"  [cyan]{Markup.Escape(sc.Id)}[/] ({sc.Kind}, host=[white]{Markup.Escape(sc.Host ?? "n/a")}[/]) ");
            if (!sc.Enabled)
            {
                AnsiConsole.MarkupLine("[grey]disabled[/]");
                continue;
            }

            try
            {
                ITokenProvider provider = sc.Kind switch
                {
                    SourceConfigKind.GitHub or SourceConfigKind.GitHubEnterprise =>
                        new GhCliTokenProvider(sc.Id, sc.Host ?? throw new InvalidOperationException("host required")),
                    SourceConfigKind.AzureDevOps =>
                        new AzureCliTokenProvider(sc.Id),
                    _ => throw new InvalidOperationException($"Unknown kind {sc.Kind}"),
                };

                var token = await provider.GetTokenAsync();
                var identity = await provider.GetAuthenticatedIdentityAsync();
                AnsiConsole.MarkupLine($"[green]ok[/] (token length {token.Length}, identity: [white]{Markup.Escape(identity ?? "<unknown>")}[/])");
            }
            catch (TokenAcquisitionException ex)
            {
                allOk = false;
                AnsiConsole.MarkupLine("[red]failed[/]");
                var firstLine = ex.Message.Split('\n')[0];
                AnsiConsole.MarkupLine($"    [grey]{Markup.Escape(firstLine)}[/]");
            }
        }

        AnsiConsole.WriteLine();
        if (config.Ado.Projects.Count > 0)
        {
            AnsiConsole.MarkupLine($"[grey]ADO projects configured: {config.Ado.Projects.Count}[/]");
            foreach (var p in config.Ado.Projects)
            {
                AnsiConsole.MarkupLine($"  - {Markup.Escape(p.Org)}/{Markup.Escape(p.Project)}");
            }
            AnsiConsole.MarkupLine("[yellow]Note: the ADO source adapter is not yet implemented in v0.1.[/]");
        }

        return allOk ? 0 : 1;
    }
}

internal sealed class ConfigInitSettings : CommandSettings
{
    [CommandOption("--config <PATH>")]
    public string? ConfigPath { get; init; }
}

internal sealed class ConfigInitCommand : AsyncCommand<ConfigInitSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ConfigInitSettings settings)
    {
        var path = settings.ConfigPath ?? PrInboxConfig.DefaultPath;
        if (File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[yellow]Config already exists at {Markup.Escape(path)}.[/]");
            AnsiConsole.MarkupLine("Edit it directly, or use the [bold]config add-source[/] commands.");
            return 0;
        }

        var seed = new PrInboxConfig
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
            },
            Bots = new BotConfig { ExtraLogins = { "Copilot" } },
        };

        await seed.SaveAsync(path);
        AnsiConsole.MarkupLine($"[green]Initialized config[/] at [cyan]{Markup.Escape(path)}[/]");
        AnsiConsole.MarkupLine("[grey]Edit to add more sources or run [bold]pr-inbox config doctor[/] to verify auth.[/]");
        return 0;
    }
}

internal sealed class AddSourceSettings : CommandSettings
{
    [CommandArgument(0, "<KIND>")]
    [Description("Source kind: github | github-enterprise | azure-devops")]
    public required string Kind { get; init; }

    [CommandArgument(1, "<HOST_OR_ORG>")]
    [Description("GitHub: hostname (e.g. github.com or github.contoso.com). ADO: org name.")]
    public required string HostOrOrg { get; init; }

    [CommandOption("--id <ID>")]
    public string? Id { get; init; }

    [CommandOption("--config <PATH>")]
    public string? ConfigPath { get; init; }
}

internal sealed class AddSourceCommand : AsyncCommand<AddSourceSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, AddSourceSettings settings)
    {
        var config = await PrInboxConfig.LoadAsync(settings.ConfigPath);
        var kind = settings.Kind.ToLowerInvariant() switch
        {
            "github" => SourceConfigKind.GitHub,
            "github-enterprise" or "ghe" => SourceConfigKind.GitHubEnterprise,
            "azure-devops" or "ado" => SourceConfigKind.AzureDevOps,
            _ => throw new ArgumentException($"Unknown kind '{settings.Kind}'."),
        };

        var id = settings.Id ?? kind switch
        {
            SourceConfigKind.GitHub => settings.HostOrOrg == "github.com" ? "gh.com" : $"gh.{settings.HostOrOrg}",
            SourceConfigKind.GitHubEnterprise => $"ghe.{settings.HostOrOrg}",
            SourceConfigKind.AzureDevOps => $"ado:{settings.HostOrOrg}",
            _ => settings.HostOrOrg,
        };

        if (config.Sources.Any(s => s.Id == id))
        {
            AnsiConsole.MarkupLine($"[yellow]Source '{Markup.Escape(id)}' already exists.[/]");
            return 0;
        }

        config.Sources.Add(new SourceConfig
        {
            Id = id,
            Kind = kind,
            Host = kind == SourceConfigKind.AzureDevOps ? null : settings.HostOrOrg,
            Identity = "default",
            Enabled = true,
        });

        await config.SaveAsync(settings.ConfigPath);
        AnsiConsole.MarkupLine($"[green]Added source[/] [cyan]{Markup.Escape(id)}[/] ({kind})");
        return 0;
    }
}

internal sealed class AddAdoProjectSettings : CommandSettings
{
    [CommandArgument(0, "<ORG>")]
    public required string Org { get; init; }

    [CommandArgument(1, "<PROJECT>")]
    public required string Project { get; init; }

    [CommandOption("--config <PATH>")]
    public string? ConfigPath { get; init; }
}

internal sealed class AddAdoProjectCommand : AsyncCommand<AddAdoProjectSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, AddAdoProjectSettings settings)
    {
        var config = await PrInboxConfig.LoadAsync(settings.ConfigPath);
        if (config.Ado.Projects.Any(p => p.Org == settings.Org && p.Project == settings.Project))
        {
            AnsiConsole.MarkupLine("[yellow]Already configured.[/]");
            return 0;
        }
        config.Ado.Projects.Add(new AdoProjectConfig { Org = settings.Org, Project = settings.Project });
        await config.SaveAsync(settings.ConfigPath);
        AnsiConsole.MarkupLine($"[green]Added ADO project[/] [cyan]{Markup.Escape(settings.Org)}/{Markup.Escape(settings.Project)}[/]");
        return 0;
    }
}
