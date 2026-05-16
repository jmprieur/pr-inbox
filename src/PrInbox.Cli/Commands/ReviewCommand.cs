using System.ComponentModel;
using PrInbox.Core.Reviewing;
using PrInbox.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PrInbox.Cli.Commands;

internal sealed class ReviewSettings : CommandSettings
{
    [CommandArgument(0, "<PR_URL>")]
    [Description("PR URL, e.g. https://github.com/owner/repo/pull/42")]
    public required string PrId { get; init; }

    [CommandOption("--refresh")]
    [Description("Re-sync this PR before generating the brief.")]
    public bool Refresh { get; init; } = true;
}

internal sealed class ReviewCommand : AsyncCommand<ReviewSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ReviewSettings settings)
    {
        var db = new PrInboxDb(PrInboxDb.DefaultUserConnectionString());
        await new MigrationRunner().MigrateAsync(db.ConnectionString);

        var briefService = new BriefService(
            new PullRequestRepository(db),
            new PrSnapshotRepository(db),
            new ObservedThreadRepository(db),
            new ReviewRunRepository(db));

        BriefResult result;
        try
        {
            result = await briefService.CreateBriefAsync(settings.PrId, CancellationToken.None);
        }
        catch (BriefCreationException ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]Review run #{result.RunId} created.[/]");
        AnsiConsole.MarkupLine($"  brief:    [cyan]{Markup.Escape(result.BriefPath)}[/]");
        AnsiConsole.MarkupLine($"  metadata: [cyan]{Markup.Escape(result.MetadataPath)}[/]");
        AnsiConsole.MarkupLine($"  HEAD:     [white]{Markup.Escape(result.HeadSha)}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Recommended invocation:[/]");
        AnsiConsole.MarkupLine($"  [grey]copilot --prompt \"{Markup.Escape(result.BriefPath)}\"[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey](Copy the path into a fresh Copilot session and ask it to use the dual-model-review agent.)[/]");
        return 0;
    }
}
