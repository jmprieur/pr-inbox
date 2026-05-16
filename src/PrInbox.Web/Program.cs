using PrInbox.Web.Components;
using PrInbox.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Blazor Server (interactive server) — SignalR is automatically wired.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// pr-inbox singletons. The Web project drives SQLite + sync the same
// way the CLI does; no shared state file beyond %APPDATA%\PrInbox.
builder.Services.AddSingleton<InboxState>();
builder.Services.AddSingleton<IReviewLauncher, ReviewLauncher>();
builder.Services.AddHostedService<InboxSyncHostedService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
