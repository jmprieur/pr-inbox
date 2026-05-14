using PrInbox.Cli.Commands;
using PrInbox.Cli.SmokeTools;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PrInbox.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // Legacy smoke entry points (preserved while CLI command app stabilizes).
        if (args.Length > 0 && args[0] == "--smoke-tokens")
        {
            return await TokenSmoke.RunAsync(args);
        }
        if (args.Length > 0 && args[0] == "--smoke-github")
        {
            return await GitHubSmoke.RunAsync(args);
        }

        var app = new CommandApp();
        app.Configure(config =>
        {
            config.SetApplicationName("pr-inbox");
            config.AddCommand<SyncCommand>("sync")
                .WithDescription("Pull PRs assigned to me as reviewer from all enabled sources.");
            config.AddCommand<ListCommand>("list")
                .WithDescription("Show triage table of tracked PRs.");
            config.AddCommand<ReviewCommand>("review")
                .WithDescription("Generate an immutable review brief and a copilot invocation.");
            config.AddBranch("config", cfg =>
            {
                cfg.SetDescription("Manage sources, ADO projects, and auth diagnostics.");
                cfg.AddCommand<ConfigInitCommand>("init")
                    .WithDescription("Seed an empty config.json at the default location.");
                cfg.AddCommand<ConfigDoctorCommand>("doctor")
                    .WithDescription("Verify gh + az auth for every enabled source.");
                cfg.AddCommand<AddSourceCommand>("add-source")
                    .WithDescription("Add a github / github-enterprise / azure-devops source.");
                cfg.AddCommand<AddAdoProjectCommand>("add-ado-project")
                    .WithDescription("Register an (org, project) pair for ADO enumeration.");
            });
        });

        return await app.RunAsync(args);
    }
}

