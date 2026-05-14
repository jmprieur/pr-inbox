using PrInbox.Core.Credentials;
using PrInbox.Core.Models;
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
                    var tokenProvider = new GhCliTokenProvider(sc.Id, sc.Host);
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
                    var tokenProvider = new GhCliTokenProvider(sc.Id, sc.Host);
                    var source = new GitHubReadSource(sc.Id, sc.Host, isEnterprise: true, tokenProvider, botDetector);
                    result.Add(new RuntimeSource(source, tokenProvider, sc.Identity));
                    break;
                }
                case SourceConfigKind.AzureDevOps:
                {
                    throw new NotImplementedException(
                        $"Azure DevOps source '{sc.Id}' is configured but the ADO adapter is not yet implemented in v0.1. " +
                        "GitHub sources work; ADO will land in v0.1.5. See AMBIGUITIES.md.");
                }
            }
        }
        return result;
    }
}

public sealed record RuntimeSource(IPrReadSource Source, ITokenProvider TokenProvider, string Identity);
