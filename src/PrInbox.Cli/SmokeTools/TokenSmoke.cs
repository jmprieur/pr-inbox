using PrInbox.Core.Credentials;
using Spectre.Console;

namespace PrInbox.Cli.SmokeTools;

internal static class TokenSmoke
{
    public static async Task<int> RunAsync(string[] args)
    {
        AnsiConsole.MarkupLine("[bold]Token provider smoke test[/]");
        AnsiConsole.WriteLine();

        var allOk = true;
        allOk &= await TestGitHubAsync("gh.com", "github.com");
        allOk &= await TestAzureAsync("ado:test");

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(allOk
            ? "[green]All token providers acquired a token successfully.[/]"
            : "[red]At least one token provider failed.[/]");
        return allOk ? 0 : 1;
    }

    private static async Task<bool> TestGitHubAsync(string sourceId, string hostname)
    {
        AnsiConsole.Markup($"[grey]GitHub source[/] [cyan]{sourceId}[/] (host [white]{hostname}[/])... ");
        var provider = new GhCliTokenProvider(sourceId, hostname);
        try
        {
            var token = await provider.GetTokenAsync();
            var login = await provider.GetAuthenticatedIdentityAsync();
            AnsiConsole.MarkupLine($"[green]ok[/] (token length {token.Length}, identity: [white]{login ?? "<unknown>"}[/])");
            return true;
        }
        catch (TokenAcquisitionException ex)
        {
            AnsiConsole.MarkupLine($"[red]failed[/] - {Markup.Escape(ex.Message.Split('\n')[0])}");
            return false;
        }
    }

    private static async Task<bool> TestAzureAsync(string sourceId)
    {
        AnsiConsole.Markup($"[grey]Azure DevOps source[/] [cyan]{sourceId}[/]... ");
        var provider = new AzureCliTokenProvider(sourceId);
        try
        {
            var token = await provider.GetTokenAsync();
            var name = await provider.GetAuthenticatedIdentityAsync();
            AnsiConsole.MarkupLine($"[green]ok[/] (token length {token.Length}, identity: [white]{name ?? "<unknown>"}[/])");
            return true;
        }
        catch (TokenAcquisitionException ex)
        {
            AnsiConsole.MarkupLine($"[red]failed[/] - {Markup.Escape(ex.Message.Split('\n')[0])}");
            return false;
        }
    }
}
