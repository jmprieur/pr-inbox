using Microsoft.Extensions.Logging;
using PrInbox.Core.Credentials;
using PrInbox.Core.Storage;
using PrInbox.Publishers;

namespace PrInbox.Web.Services;

/// <summary>
/// Factory that builds the (URL, identity) → publisher dictionary from
/// <see cref="PrInboxConfig"/> at app start. Wires one
/// <see cref="GitHubReviewPublisher"/> per (host, identity) and one
/// <see cref="AdoReviewPublisher"/> for dev.azure.com.
/// </summary>
/// <remarks>
/// The default identity per host is chosen with the documented preference
/// EMU > Proxima > public for github.com (so URL-only lookups land on the
/// EMU token), and the lone ADO identity wins for dev.azure.com.
/// </remarks>
public static class PublisherWiring
{
    public static IPublisherSelector BuildSelector(
        PrInboxConfig config,
        ILoggerFactory logFactory,
        HttpClient sharedClient)
    {
        var byPair = new Dictionary<(string Host, string Identity), IPrReviewPublisher>();
        var defaultByHost = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in config.Sources.Where(s => s.Enabled))
        {
            switch (source.Kind)
            {
                case SourceConfigKind.GitHub:
                case SourceConfigKind.GitHubEnterprise:
                {
                    if (string.IsNullOrWhiteSpace(source.Host)) continue;
                    var host = source.Host!.ToLowerInvariant();
                    var isEnterprise = source.Kind == SourceConfigKind.GitHubEnterprise;
                    var tokens = new GhCliTokenProvider(
                        sourceId: source.Id,
                        hostname: host,
                        identity: source.Identity == "default" ? null : source.Identity,
                        logger: logFactory.CreateLogger<GhCliTokenProvider>());
                    var publisher = new GitHubReviewPublisher(
                        tokens, sharedClient, isEnterprise, host, source.Identity,
                        logFactory.CreateLogger<GitHubReviewPublisher>());
                    byPair[(host, source.Identity)] = publisher;
                    // Default identity per host: prefer EMU/internal if multiple.
                    if (!defaultByHost.TryGetValue(host, out var existing) ||
                        IsPreferredIdentity(source.Identity, existing))
                    {
                        defaultByHost[host] = source.Identity;
                    }
                    break;
                }
                case SourceConfigKind.AzureDevOps:
                {
                    // ADO uses Azure CLI credentials; identity tagged with org.
                    const string host = "dev.azure.com";
                    var tokens = new AzureCliTokenProvider(
                        sourceId: source.Id,
                        logger: logFactory.CreateLogger<AzureCliTokenProvider>());
                    var publisher = new AdoReviewPublisher(
                        tokens, sharedClient, identityUsed: source.Identity,
                        logFactory.CreateLogger<AdoReviewPublisher>());
                    byPair[(host, source.Identity)] = publisher;
                    if (!defaultByHost.ContainsKey(host))
                    {
                        defaultByHost[host] = source.Identity;
                    }
                    break;
                }
            }
        }

        return new ConfigDrivenPublisherSelector(
            byPair, defaultByHost,
            logFactory.CreateLogger<ConfigDrivenPublisherSelector>());
    }

    // Preference: identities containing "microsoft" or "msft" (EMU) >
    // any other named identity > "default".
    private static bool IsPreferredIdentity(string candidate, string current)
    {
        int Rank(string s) => s switch
        {
            _ when s.Contains("microsoft", StringComparison.OrdinalIgnoreCase) => 3,
            _ when s.Contains("msft", StringComparison.OrdinalIgnoreCase) => 3,
            "default" => 0,
            _ => 1,
        };
        return Rank(candidate) > Rank(current);
    }
}
