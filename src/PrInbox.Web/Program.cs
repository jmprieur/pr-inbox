using PrInbox.Core.Credentials;
using PrInbox.Core.Storage;
using PrInbox.Publishers;
using PrInbox.Web.Components;
using PrInbox.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Blazor Server (interactive server) — SignalR is automatically wired.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// pr-inbox singletons. The Web project drives SQLite + sync the same
// way the CLI does; no shared state file beyond %APPDATA%\PrInbox.
builder.Services.AddSingleton<InboxState>();
builder.Services.AddSingleton<ReviewRunStore>();
builder.Services.AddSingleton<IReviewLauncher, ReviewLauncher>();
builder.Services.AddHostedService<InboxSyncHostedService>();

// Publishers — only registered here (CLI never references PrInbox.Publishers).
// Migrate the DB up-front so chunk-7 schema (migration v4) is in place
// before any UI POST hits the orchestrator. Also pre-load PrInboxConfig
// synchronously here so the IPublisherSelector factory below stays
// fully synchronous (no .GetAwaiter().GetResult() inside the HTTP
// pipeline, which is a classic Blazor deadlock).
var bootDb = new PrInboxDb(PrInboxDb.DefaultUserConnectionString());
new MigrationRunner().MigrateAsync(bootDb.ConnectionString).GetAwaiter().GetResult();
var bootCfg = PrInboxConfig.LoadAsync().GetAwaiter().GetResult();

builder.Services.AddSingleton(bootDb);
builder.Services.AddSingleton(bootCfg);
builder.Services.AddSingleton(sp => new PullRequestRepository(sp.GetRequiredService<PrInboxDb>()));
builder.Services.AddSingleton(sp => new PostedReviewRepository(sp.GetRequiredService<PrInboxDb>()));

builder.Services.AddHttpClient("publisher", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<IPublisherSelector>(sp =>
{
    var cfg = sp.GetRequiredService<PrInboxConfig>();
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("publisher");
    return PublisherWiring.BuildSelector(cfg, loggerFactory, http);
});
builder.Services.AddSingleton<ReviewPublishOrchestrator>(sp => new ReviewPublishOrchestrator(
    sp.GetRequiredService<IPublisherSelector>(),
    sp.GetRequiredService<PullRequestRepository>(),
    sp.GetRequiredService<PostedReviewRepository>(),
    sp.GetRequiredService<ILogger<ReviewPublishOrchestrator>>()));

var app = builder.Build();

// Force-resolve the orchestrator + selector at startup so the
// publisher dictionary is built outside any HTTP request context.
_ = app.Services.GetRequiredService<IPublisherSelector>();
_ = app.Services.GetRequiredService<ReviewPublishOrchestrator>();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Re-attach watchers to in-flight runs so a restart doesn't lose findings.
if (app.Services.GetRequiredService<IReviewLauncher>() is ReviewLauncher rl)
{
    rl.RehydrateInFlightRuns();
}

app.Run();
