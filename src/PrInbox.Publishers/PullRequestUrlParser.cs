using System.Text.RegularExpressions;

namespace PrInbox.Publishers;

/// <summary>
/// Parses a canonical PR URL into its platform-specific pieces.
/// Only handles URL shapes used by pr-inbox (after URL canonicalisation).
/// </summary>
public static class PullRequestUrlParser
{
    private static readonly Regex GitHubLikePattern =
        new(@"^https://(?<host>[^/]+)/(?<owner>[^/]+)/(?<repo>[^/]+)/pull/(?<num>\d+)$",
            RegexOptions.Compiled);

    private static readonly Regex AdoPattern =
        new(@"^https://dev\.azure\.com/(?<org>[^/]+)/(?<project>[^/]+)/_git/(?<repo>[^/]+)/pullrequest/(?<num>\d+)",
            RegexOptions.Compiled);

    public static PrUrlRef Parse(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("URL is empty.", nameof(url));
        }

        var adoMatch = AdoPattern.Match(url);
        if (adoMatch.Success)
        {
            return new PrUrlRef(
                Kind: PrPlatform.AzureDevOps,
                Host: "dev.azure.com",
                Owner: adoMatch.Groups["org"].Value,
                Repo: adoMatch.Groups["repo"].Value,
                Number: int.Parse(adoMatch.Groups["num"].Value),
                AdoProject: adoMatch.Groups["project"].Value,
                CanonicalUrl: url);
        }

        var ghMatch = GitHubLikePattern.Match(url);
        if (ghMatch.Success)
        {
            var host = ghMatch.Groups["host"].Value;
            var kind = host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                ? PrPlatform.GitHubDotCom
                : PrPlatform.GitHubEnterprise;
            return new PrUrlRef(
                Kind: kind,
                Host: host,
                Owner: ghMatch.Groups["owner"].Value,
                Repo: ghMatch.Groups["repo"].Value,
                Number: int.Parse(ghMatch.Groups["num"].Value),
                AdoProject: null,
                CanonicalUrl: url);
        }

        throw new ArgumentException($"Cannot parse PR URL: {url}", nameof(url));
    }
}

public sealed record PrUrlRef(
    PrPlatform Kind,
    string Host,
    string Owner,
    string Repo,
    int Number,
    string? AdoProject,
    string CanonicalUrl);

public enum PrPlatform
{
    GitHubDotCom,
    GitHubEnterprise,
    AzureDevOps,
}
