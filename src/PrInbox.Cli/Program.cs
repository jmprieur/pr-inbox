using System.Reflection;
using Spectre.Console;

namespace PrInbox.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        AnsiConsole.Write(new FigletText("pr-inbox").Color(Color.Aqua));
        AnsiConsole.MarkupLine($"[grey]v{Assembly.GetExecutingAssembly().GetName().Version}[/]");
        AnsiConsole.WriteLine();

        // TODO(Phase 3): wire Spectre.Console.Cli command app and commands.
        AnsiConsole.MarkupLine("[yellow]pr-inbox is being built. Commands will arrive in upcoming phases.[/]");
        AnsiConsole.MarkupLine("[grey]Run [bold]pr-inbox --help[/] after Phase 3 lands.[/]");
        return 0;
    }
}
