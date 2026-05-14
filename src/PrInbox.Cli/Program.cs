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

        AnsiConsole.Write(new FigletText("pr-inbox").Color(Color.Aqua));
        AnsiConsole.MarkupLine($"[grey]v{Assembly.GetExecutingAssembly().GetName().Version}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]pr-inbox is being built. Commands will arrive in upcoming phases.[/]");
        AnsiConsole.MarkupLine("[grey]Try: [bold]pr-inbox --smoke-tokens[/] to verify gh + az auth.[/]");
        return 0;
    }
}

