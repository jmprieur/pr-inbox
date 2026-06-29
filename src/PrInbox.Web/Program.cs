using PrInbox.Core.Config;
using PrInbox.Core.Credentials;
using PrInbox.Core.Storage;
using PrInbox.Publishers;
using PrInbox.Web.Components;
using PrInbox.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Enable static web assets in all environments, not just Development.
// Without this, running `dotnet run` against build output (not publish
// output) in Production mode causes /_framework/blazor.web.js to throw
// FileNotFoundException, killing Blazor Server interactivity. This is
// the dev-time fallback path; published apps don't need it but it's a
// no-op there.
builder.WebHost.UseStaticWebAssets();

// Blazor Server (interactive server) — SignalR is automatically wired.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// pr-inbox singletons. The Web project drives SQLite + sync the same
// way the CLI does; no shared state file beyond %APPDATA%\PrInbox.
builder.Services.AddSingleton<InboxState>();
builder.Services.AddSingleton<ReviewRunStore>();
builder.Services.AddSingleton<ConsoleWindowRegistry>();
builder.Services.AddSingleton<IReviewLauncher, ReviewLauncher>();
builder.Services.AddSingleton<InboxSyncHostedService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<InboxSyncHostedService>());

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
builder.Services.AddSingleton(sp => new UiPreferencesRepository(sp.GetRequiredService<PrInboxDb>()));
builder.Services.AddSingleton(sp => new ObservedThreadRepository(sp.GetRequiredService<PrInboxDb>()));
builder.Services.AddSingleton(sp => new PrSnapshotRepository(sp.GetRequiredService<PrInboxDb>()));
builder.Services.AddSingleton(sp => new TagRepository(sp.GetRequiredService<PrInboxDb>()));

// Config service: read/write façade over PrInboxConfig used by the Settings
// page. Mutations persist to disk and mirror back onto the singleton so
// live consumers (Inbox.razor IgnoredRepos read, etc.) refresh in-place.
builder.Services.AddSingleton<IConfigService>(sp =>
    new ConfigService(sp.GetRequiredService<PrInboxConfig>()));

// gh-CLI based identity discovery for the Settings UX. Used when the
// user clicks "+ Add GitHub.com" so we can offer a picker of available
// logins instead of hard-coding "default". Failures (gh missing, no
// logins) collapse to an empty list, which the UI handles by falling
// back to the legacy default-identity add.
builder.Services.AddSingleton<IGhCliRunner, GhCliRunner>();
builder.Services.AddSingleton<IGitHubAuthDiscovery, GhCliGitHubAuthDiscovery>();
builder.Services.AddSingleton<IGitHubRateLimitProbe, GhCliRateLimitProbe>();
builder.Services.AddSingleton<DoctorService>();

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
builder.Services.AddSingleton<ThreadResolveOrchestrator>(sp => new ThreadResolveOrchestrator(
    sp.GetRequiredService<IPublisherSelector>(),
    sp.GetRequiredService<PullRequestRepository>(),
    sp.GetRequiredService<ObservedThreadRepository>(),
    sp.GetRequiredService<ILogger<ThreadResolveOrchestrator>>()));

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

// Lightweight liveness probe used by tools/splash.html to know when the
// web app is up and ready to receive a real navigation. Returns 200 as
// soon as Kestrel is listening and the DI graph is built (both true by
// the time the request reaches this handler), so the splash can stop
// polling. No CORS headers are set because the splash uses fetch with
// mode: 'no-cors' — it only needs to know the connection succeeded.
app.MapGet("/healthz", () => Results.Text("ok"));

// Loopback-only graceful shutdown hook used by the system-tray launcher
// (PrInbox.Tray) to stop the app cleanly. Going through here fires
// ApplicationStopping -> ConsoleWindowRegistry.RestoreAll(), so review
// consoles get un-minimized; a hard Kill() from the tray would orphan them.
// Non-loopback callers are rejected. When the launcher injects
// PRINBOX_SHUTDOWN_TOKEN, a matching X-Shutdown-Token header is also required
// so a stray local web page can't POST the app into shutting down.
app.MapPost("/shutdown", (HttpContext ctx, IHostApplicationLifetime lifetime) =>
{
    var remote = ctx.Connection.RemoteIpAddress;
    if (remote is null || !System.Net.IPAddress.IsLoopback(remote))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    // Fail closed: the tray launcher (the only legitimate caller) ALWAYS sets
    // PRINBOX_SHUTDOWN_TOKEN. If it's absent we're running under `dotnet run`
    // or similar; rejecting here stops a stray web page POST from killing the
    // app via a CORS-simple request (no preflight, no custom header needed).
    var expected = Environment.GetEnvironmentVariable("PRINBOX_SHUTDOWN_TOKEN");
    if (string.IsNullOrEmpty(expected))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }
    var provided = ctx.Request.Headers["X-Shutdown-Token"].ToString();
    var match = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
        System.Text.Encoding.UTF8.GetBytes(provided),
        System.Text.Encoding.UTF8.GetBytes(expected));
    if (!match)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    // Defer so the 202 response flushes before Kestrel starts draining.
    _ = Task.Run(async () =>
    {
        await Task.Delay(250);
        lifetime.StopApplication();
    });
    return Results.Accepted();
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Re-attach watchers to in-flight runs so a restart doesn't lose findings.
if (app.Services.GetRequiredService<IReviewLauncher>() is ReviewLauncher rl)
{
    rl.RehydrateInFlightRuns();
}

// Belt-and-braces: on graceful shutdown, restore every minimized console
// so a pr-inbox exit never leaves a review window orphaned out of view.
app.Lifetime.ApplicationStopping.Register(() =>
{
    try { app.Services.GetRequiredService<ConsoleWindowRegistry>().RestoreAll(); }
    catch { /* shutdown best-effort */ }
});

app.Run();
