using System.Reflection;
using PrInbox.Cli.SmokeTools;
using Spectre.Console;

namespace PrInbox.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--smoke-tokens")
        {
            return await TokenSmoke.RunAsync(args);
        }
        if (args.Length > 0 && args[0] == "--smoke-github")
        {
            return await GitHubSmoke.RunAsync(args);
        }

        AnsiConsole.Write(new FigletText("pr-inbox").Color(Color.Aqua));
        AnsiConsole.MarkupLine($"[grey]v{Assembly.GetExecutingAssembly().GetName().Version}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]pr-inbox is being built. Commands will arrive in upcoming phases.[/]");
        AnsiConsole.MarkupLine("[grey]Smoke tools (temporary):[/]");
        AnsiConsole.MarkupLine("[grey]  [bold]pr-inbox --smoke-tokens[/] - verify gh + az auth[/]");
        AnsiConsole.MarkupLine("[grey]  [bold]pr-inbox --smoke-github[/] - live read of your github.com inbox[/]");
        return 0;
    }
}

