using PrInbox.Core.Credentials;
using PrInbox.Sources.GitHub;
using Spectre.Console;

namespace PrInbox.Cli.SmokeTools;

/// <summary>
/// Live read-only test against the user's real github.com inbox.
/// Read-only by construction (uses IPrReadSource).
/// </summary>
internal static class GitHubSmoke
{
    public static async Task<int> RunAsync(string[] args)
    {
        var tokenProvider = new GhCliTokenProvider("gh.com", "github.com");
        var source = new GitHubReadSource(
            sourceId: "gh.com",
            hostname: "github.com",
            isEnterprise: false,
            tokenProvider: tokenProvider,
            botDetector: new BotDetector(new[] { "Copilot" }));

        AnsiConsole.MarkupLine("[bold]GitHub adapter smoke test[/]");
        AnsiConsole.MarkupLine($"[grey]Source: {source.SourceId}, kind: {source.Kind}[/]");
        AnsiConsole.WriteLine();

        try
        {
            AnsiConsole.Markup("[grey]Fetching review inbox...[/] ");
            var inbox = await source.GetReviewInboxAsync(CancellationToken.None);
            AnsiConsole.MarkupLine($"[green]{inbox.Count} PR(s)[/]");

            if (inbox.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No PRs currently assigned to you as reviewer.[/]");
                return 0;
            }

            var table = new Table()
                .AddColumn("PR")
                .AddColumn("Repo")
                .AddColumn("Title")
                .AddColumn("Author")
                .AddColumn("Updated");
            foreach (var pr in inbox.Take(10))
            {
                table.AddRow(
                    Markup.Escape(pr.Identity.Display),
                    Markup.Escape(pr.DisplayRepo),
                    Markup.Escape(Truncate(pr.Title ?? "", 60)),
                    Markup.Escape(pr.AuthorLogin ?? ""),
                    pr.LastUpdated.ToString("yyyy-MM-dd HH:mm"));
            }
            AnsiConsole.Write(table);
            if (inbox.Count > 10)
            {
                AnsiConsole.MarkupLine($"[grey](showing 10 of {inbox.Count})[/]");
            }
            AnsiConsole.WriteLine();

            // Pick the first PR and exercise detail / threads / commits.
            var sample = inbox[0];
            AnsiConsole.MarkupLine($"[bold]Detail for [cyan]{Markup.Escape(sample.Identity.Display)}[/][/]");

            var detail = await source.GetPullRequestDetailAsync(sample.Identity, CancellationToken.None);
            AnsiConsole.MarkupLine($"  head: [white]{detail.HeadSha[..Math.Min(12, detail.HeadSha.Length)]}[/]");
            AnsiConsole.MarkupLine($"  base: [white]{detail.BaseSha[..Math.Min(12, detail.BaseSha.Length)]}[/]");
            AnsiConsole.MarkupLine($"  commits: [white]{detail.OrderedCommitShas.Count}[/]");
            AnsiConsole.MarkupLine($"  reviewer state: [white]{detail.ReviewerState}[/]");

            var threads = await source.GetThreadsAsync(sample.Identity, CancellationToken.None);
            var bots = threads.Count(t => t.IsBot);
            AnsiConsole.MarkupLine($"  threads: [white]{threads.Count}[/] (bot: [white]{bots}[/])");

            foreach (var t in threads.Where(t => t.IsBot).Take(3))
            {
                AnsiConsole.MarkupLine($"    [grey]{t.Kind}[/] [yellow]{Markup.Escape(t.AuthorLogin ?? "")}[/] ({t.BotKind})");
            }

            AnsiConsole.MarkupLine("[green]GitHub adapter smoke test passed.[/]");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed: {Markup.Escape(ex.GetType().Name)}: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";
}
