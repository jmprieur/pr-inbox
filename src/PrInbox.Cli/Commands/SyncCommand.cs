using System.ComponentModel;
using PrInbox.Core.Credentials;
using PrInbox.Core.Storage;
using PrInbox.Sources;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PrInbox.Cli.Commands;

internal sealed class SyncSettings : CommandSettings
{
    [CommandOption("--source <SOURCE_ID>")]
    [Description("Limit sync to a single source id (e.g. gh.com).")]
    public string? SourceId { get; init; }

    [CommandOption("--config <PATH>")]
    [Description("Path to config.json. Defaults to %APPDATA%\\PrInbox\\config.json.")]
    public string? ConfigPath { get; init; }
}

internal sealed class SyncCommand : AsyncCommand<SyncSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, SyncSettings settings)
    {
        var config = await PrInboxConfig.LoadAsync(settings.ConfigPath);
        if (config.Sources.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No sources configured.[/] Run [bold]pr-inbox config init[/] then add sources.");
            return 1;
        }

        var db = new PrInboxDb(PrInboxDb.DefaultUserConnectionString());
        await new MigrationRunner().MigrateAsync(db.ConnectionString);

        var prRepo = new PullRequestRepository(db);
        var snapRepo = new PrSnapshotRepository(db);
        var threadRepo = new ObservedThreadRepository(db);
        var syncRunRepo = new SyncRunRepository(db);

        var sourceFactory = new SourceFactory();
        IReadOnlyList<RuntimeSource> runtimes;
        try
        {
            runtimes = sourceFactory.Build(config);
        }
        catch (NotImplementedException ex)
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(ex.Message)}[/]");
            AnsiConsole.MarkupLine("[grey]Proceeding with the GitHub sources that ARE supported.[/]");
            runtimes = sourceFactory.Build(SkipAdoSources(config));
        }

        if (settings.SourceId is not null)
        {
            runtimes = runtimes.Where(r => r.Source.SourceId == settings.SourceId).ToList();
            if (runtimes.Count == 0)
            {
                AnsiConsole.MarkupLine($"[red]No enabled source with id '{Markup.Escape(settings.SourceId)}'.[/]");
                return 1;
            }
        }

        AnsiConsole.MarkupLine($"[bold]Syncing {runtimes.Count} source(s)...[/]");
        AnsiConsole.WriteLine();

        var results = new List<SyncResult>();
        foreach (var rt in runtimes)
        {
            var orchestrator = new SyncOrchestrator(rt.Source, prRepo, snapRepo, threadRepo, syncRunRepo);
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"[cyan]{rt.Source.SourceId}[/]: starting...", async ctx =>
                {
                    var progress = new Progress<SyncProgress>(p =>
                    {
                        var prefix = p.PrsTotal is not null ? $"{p.PrsSeen}/{p.PrsTotal} " : "";
                        ctx.Status($"[cyan]{Markup.Escape(rt.Source.SourceId)}[/]: {prefix}{Markup.Escape(p.Message)}");
                    });
                    var result = await orchestrator.RunAsync(rt.Identity, progress, CancellationToken.None);
                    results.Add(result);
                });

            var last = results[^1];
            var color = last.Status switch
            {
                Core.Models.SyncRunStatus.Ok => "green",
                Core.Models.SyncRunStatus.Partial => "yellow",
                _ => "red",
            };
            AnsiConsole.MarkupLine($"  [{color}]{rt.Source.SourceId}[/]: {last.Status}, {last.PrsSeen} PR(s)" +
                (last.PrsFailed > 0 ? $", [red]{last.PrsFailed} failed[/]" : "") +
                (last.Error is not null ? $" [grey]({Markup.Escape(last.Error)})[/]" : ""));
        }

        AnsiConsole.WriteLine();
        var anyFailed = results.Any(r => r.Status is Core.Models.SyncRunStatus.Failed);
        AnsiConsole.MarkupLine(anyFailed
            ? "[yellow]Sync completed with errors.[/]"
            : "[green]Sync completed.[/]");
        return anyFailed ? 1 : 0;
    }

    private static PrInboxConfig SkipAdoSources(PrInboxConfig config) =>
        new()
        {
            SchemaVersion = config.SchemaVersion,
            Sources = config.Sources
                .Where(s => s.Kind != SourceConfigKind.AzureDevOps)
                .ToList(),
            Ado = config.Ado,
            Bots = config.Bots,
        };
}
