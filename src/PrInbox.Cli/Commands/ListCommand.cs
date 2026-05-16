using System.ComponentModel;
using System.Text.Json;
using PrInbox.Core.Models;
using PrInbox.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PrInbox.Cli.Commands;

internal sealed class ListSettings : CommandSettings
{
    [CommandOption("--all")]
    [Description("Include closed, merged, and archived PRs.")]
    public bool All { get; init; }

    [CommandOption("--source <SOURCE_ID>")]
    [Description("Limit to a single source id.")]
    public string? SourceId { get; init; }
}

internal sealed class ListCommand : AsyncCommand<ListSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ListSettings settings)
    {
        var db = new PrInboxDb(PrInboxDb.DefaultUserConnectionString());
        await new MigrationRunner().MigrateAsync(db.ConnectionString);

        var prRepo = new PullRequestRepository(db);
        var snapRepo = new PrSnapshotRepository(db);
        var threadRepo = new ObservedThreadRepository(db);
        var syncRunRepo = new SyncRunRepository(db);

        var rows = settings.All
            ? await prRepo.ListAllAsync(CancellationToken.None)
            : await prRepo.ListActiveAsync(CancellationToken.None);

        if (settings.SourceId is not null)
        {
            rows = rows.Where(r => r.SourceId == settings.SourceId).ToList();
        }

        if (rows.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Inbox is empty.[/] Run [bold]pr-inbox sync[/] first.");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title($"[bold]pr-inbox[/]  [grey]({rows.Count} PR(s))[/]")
            .AddColumn("PR")
            .AddColumn("Title")
            .AddColumn("Age")
            .AddColumn("Churn")
            .AddColumn("Bot")
            .AddColumn("Open")
            .AddColumn(new TableColumn("Reason").Centered());

        var now = DateTimeOffset.UtcNow;

        foreach (var row in rows.OrderByDescending(r => r.LastSyncedAt))
        {
            var snapshot = await snapRepo.GetLatestAsync(row.Identity, CancellationToken.None);
            var churn = ComputeChurn(row, snapshot);
            var openThreads = await threadRepo.GetOpenThreadsAsync(row.Identity, CancellationToken.None);
            var openCount = openThreads.Count;

            var lastBriefedAt = row.LastBriefedHeadSha is null ? row.FirstSeenAt : row.LastSyncedAt;
            var newBots = await threadRepo.GetBotThreadsSinceAsync(row.Identity, lastBriefedAt, CancellationToken.None);

            table.AddRow(
                Markup.Escape(row.Identity.Url),
                Markup.Escape(Truncate(row.Title ?? "(no title)", 60)),
                FormatAge(now - row.LastSyncedAt),
                churn,
                newBots.Count == 0 ? "[grey]-[/]" : $"[yellow]{newBots.Count} new[/]",
                openCount == 0 ? "[grey]-[/]" : $"[white]{openCount}[/]",
                FormatTrackingReason(row.TrackingReason));
        }

        AnsiConsole.Write(table);

        // Source freshness footer.
        var sourceStatuses = await syncRunRepo.GetLatestPerSourceAsync(CancellationToken.None);
        if (sourceStatuses.Count > 0)
        {
            AnsiConsole.WriteLine();
            var ok = sourceStatuses.Count(s => s.Status is SyncRunStatus.Ok);
            var partial = sourceStatuses.Count(s => s.Status is SyncRunStatus.Partial);
            var failed = sourceStatuses.Count(s => s.Status is SyncRunStatus.Failed or SyncRunStatus.RateLimited);

            var footer = new List<string>();
            if (ok > 0) footer.Add($"[green]{ok} ok[/]");
            if (partial > 0) footer.Add($"[yellow]{partial} partial[/]");
            if (failed > 0) footer.Add($"[red]{failed} failed[/]");
            AnsiConsole.MarkupLine($"[grey]Sources: {string.Join(", ", footer)}[/]");

            foreach (var stale in sourceStatuses.Where(s => s.Status is not SyncRunStatus.Ok))
            {
                AnsiConsole.MarkupLine($"[yellow]  {Markup.Escape(stale.SourceId)} ({stale.Status})[/] {Markup.Escape(stale.Error ?? "")}");
            }
        }

        return 0;
    }

    private static string ComputeChurn(PullRequestRow pr, PrSnapshotRow? latest)
    {
        if (latest is null) return "[grey]?[/]";
        if (pr.LastBriefedHeadSha is null) return "[white]new[/]";
        if (pr.LastBriefedHeadSha == latest.HeadSha) return "[grey](clean)[/]";

        // Force-push detection via reachability inside latest.OrderedCommitShas.
        if (!latest.OrderedCommitShas.Contains(pr.LastBriefedHeadSha))
        {
            return "[red]force-pushed[/]";
        }
        var idx = latest.OrderedCommitShas.ToList().IndexOf(pr.LastBriefedHeadSha);
        return $"[white]+{idx} commits[/]";
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours}h";
        if (age.TotalDays < 30) return $"{(int)age.TotalDays}d";
        return $"{(int)(age.TotalDays / 30)}mo";
    }

    private static string FormatTrackingReason(TrackingReason reason) => reason switch
    {
        TrackingReason.Assigned => "[green]assigned[/]",
        TrackingReason.PreviouslyAssigned => "[grey]prev[/]",
        TrackingReason.ManuallyAdded => "[cyan]manual[/]",
        TrackingReason.Archived => "[grey]archived[/]",
        _ => reason.ToString(),
    };

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";
}
