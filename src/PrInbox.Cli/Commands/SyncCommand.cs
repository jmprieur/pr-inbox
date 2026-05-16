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
    [Description("Limit sync to a single source id (e.g. gh.com:emu).")]
    public string? SourceId { get; init; }

    [CommandOption("--config <PATH>")]
    [Description("Path to config.json. Defaults to %APPDATA%\\PrInbox\\config.json.")]
    public string? ConfigPath { get; init; }

    [CommandOption("--fast")]
    [Description("Run only tier-2 fast listing (no per-PR enrichment).")]
    public bool Fast { get; init; }

    [CommandOption("--enrich")]
    [Description("Run only tier-3 enrichment on rows already in 'basic' state.")]
    public bool Enrich { get; init; }
}

internal sealed class SyncCommand : AsyncCommand<SyncSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, SyncSettings settings)
    {
        if (settings.Fast && settings.Enrich)
        {
            AnsiConsole.MarkupLine("[red]--fast and --enrich are mutually exclusive.[/] Omit both to run a full sync.");
            return 1;
        }

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
        IReadOnlyList<RuntimeSource> runtimes = sourceFactory.Build(config);

        if (settings.SourceId is not null)
        {
            runtimes = runtimes.Where(r => r.Source.SourceId == settings.SourceId).ToList();
            if (runtimes.Count == 0)
            {
                AnsiConsole.MarkupLine($"[red]No enabled source with id '{Markup.Escape(settings.SourceId)}'.[/]");
                return 1;
            }
        }

        var mode = settings.Fast ? "fast" : settings.Enrich ? "enrich" : "full";
        AnsiConsole.MarkupLine($"[bold]Syncing {runtimes.Count} source(s) ({mode})...[/]");
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
                    var result = settings.Fast
                        ? await orchestrator.RunFastAsync(rt.Identity, progress, CancellationToken.None)
                        : settings.Enrich
                            ? await orchestrator.RunEnrichAsync(rt.Identity, progress, CancellationToken.None)
                            : await orchestrator.RunAsync(rt.Identity, progress, CancellationToken.None);
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
}
