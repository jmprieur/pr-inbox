using PrInbox.Core.Credentials;
using PrInbox.Core.Models;
using PrInbox.Sources.AzureDevOps;
using PrInbox.Sources.GitHub;

namespace PrInbox.Sources;

/// <summary>
/// Builds <see cref="IPrReadSource"/> instances from <see cref="PrInboxConfig"/>.
/// </summary>
public sealed class SourceFactory
{
    public IReadOnlyList<RuntimeSource> Build(PrInboxConfig config)
    {
        var result = new List<RuntimeSource>();
        var botDetector = new BotDetector(config.Bots.ExtraLogins);

        foreach (var sc in config.Sources.Where(s => s.Enabled))
        {
            switch (sc.Kind)
            {
                case SourceConfigKind.GitHub:
                {
                    if (string.IsNullOrWhiteSpace(sc.Host))
                    {
                        throw new InvalidOperationException(
                            $"Source '{sc.Id}' is kind=github but has no host configured.");
                    }
                    var tokenProvider = new GhCliTokenProvider(sc.Id, sc.Host, sc.Identity);
                    var source = new GitHubReadSource(sc.Id, sc.Host, isEnterprise: false, tokenProvider, botDetector);
                    result.Add(new RuntimeSource(source, tokenProvider, sc.Identity));
                    break;
                }
                case SourceConfigKind.GitHubEnterprise:
                {
                    if (string.IsNullOrWhiteSpace(sc.Host))
                    {
                        throw new InvalidOperationException(
                            $"Source '{sc.Id}' is kind=github-enterprise but has no host configured.");
                    }
                    var tokenProvider = new GhCliTokenProvider(sc.Id, sc.Host, sc.Identity);
                    var source = new GitHubReadSource(sc.Id, sc.Host, isEnterprise: true, tokenProvider, botDetector);
                    result.Add(new RuntimeSource(source, tokenProvider, sc.Identity));
                    break;
                }
                case SourceConfigKind.AzureDevOps:
                {
                    // Legacy: a SourceConfig with kind=ado is treated as a hint
                    // to enumerate config.Ado.Projects below. We do not register
                    // a runtime here. (Older configs may still carry this entry.)
                    break;
                }
            }
        }

        // ADO sources are declared as (org, project) pairs in AdoConfig.
        // Each pair becomes a separate runtime source with id ado:{org}/{project}.
        foreach (var p in config.Ado.Projects)
        {
            if (string.IsNullOrWhiteSpace(p.Org) || string.IsNullOrWhiteSpace(p.Project))
            {
                throw new InvalidOperationException("ADO project entry has empty org or project name.");
            }
            var sourceId = $"ado:{p.Org}/{p.Project}";
            var tokenProvider = new AzureCliTokenProvider(sourceId);
            var source = new AzureDevOpsReadSource(sourceId, p.Org, p.Project, tokenProvider, botDetector);
            // Identity for ADO is a single per-machine az login; we tag it as
            // "azure-cli" rather than calling out to az synchronously at factory
            // time. Per-binding identity disambiguation only matters when the
            // same source has multiple identities, which doesn't apply to ADO.
            result.Add(new RuntimeSource(source, tokenProvider, "azure-cli"));
        }

        return result;
    }
}

public sealed record RuntimeSource(IPrReadSource Source, ITokenProvider TokenProvider, string Identity);
