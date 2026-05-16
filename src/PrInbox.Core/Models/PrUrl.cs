using System.Globalization;

namespace PrInbox.Core.Models;

/// <summary>
/// Helpers for canonicalizing and parsing pull-request URLs.
/// </summary>
/// <remarks>
/// <para>
/// The canonical form rules are:
/// </para>
/// <list type="bullet">
///   <item><c>https</c> scheme (lowercased; http accepted on input but normalized to https).</item>
///   <item>Lowercased host.</item>
///   <item>No trailing slash, no query, no fragment.</item>
///   <item>Azure DevOps legacy <c>{org}.visualstudio.com</c> URLs are rewritten
///         to the modern <c>dev.azure.com/{org}</c> form.</item>
///   <item>Owner / repo / project segments preserve original case (GitHub and
///         ADO both treat them case-insensitively but display whatever the user
///         typed; we mirror that).</item>
/// </list>
/// <para>
/// Two URLs are considered the same PR if and only if their canonical forms
/// are byte-equal. Storage joins and dedupe key on <see cref="Canonicalize"/>.
/// </para>
/// </remarks>
public static class PrUrl
{
    /// <summary>
    /// Normalize a PR URL into canonical form. Throws <see cref="FormatException"/>
    /// if the URL is not a recognized PR URL.
    /// </summary>
    public static string Canonicalize(string raw) => Parse(raw).Canonical;

    /// <summary>
    /// Try to canonicalize a URL. Returns <c>false</c> (with <paramref name="canonical"/>
    /// set to <see cref="string.Empty"/>) if the URL is not a recognized PR URL.
    /// </summary>
    public static bool TryCanonicalize(string raw, out string canonical)
    {
        try
        {
            canonical = Canonicalize(raw);
            return true;
        }
        catch (FormatException)
        {
            canonical = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Parse a PR URL into its components. The returned
    /// <see cref="PrUrlComponents.Canonical"/> is always canonical.
    /// </summary>
    public static PrUrlComponents Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new FormatException("PR URL is empty.");
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            throw new FormatException($"'{raw}' is not a valid URL.");
        }

        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException($"PR URL '{raw}' must use http or https.");
        }

        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath;

        // ADO legacy host normalization: {org}.visualstudio.com → dev.azure.com/{org}
        if (host.EndsWith(".visualstudio.com", StringComparison.Ordinal))
        {
            var org = host[..^".visualstudio.com".Length];
            host = "dev.azure.com";
            path = "/" + org + path;
        }

        var segments = path.Trim('/').Split('/');

        // GitHub / GHE shape: owner/repo/pull/N
        if (segments.Length == 4 &&
            string.Equals(segments[2], "pull", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(segments[3], NumberStyles.None, CultureInfo.InvariantCulture, out var ghNumber))
        {
            var platform = host == "github.com" ? PrPlatform.GitHub : PrPlatform.GitHubEnterprise;
            var canonical = $"https://{host}/{segments[0]}/{segments[1]}/pull/{ghNumber}";
            return new PrUrlComponents(canonical, platform, host,
                Owner: segments[0], Project: null, Repo: segments[1], Number: ghNumber);
        }

        // Azure DevOps shape: org/project/_git/repo/pullrequest/N  (6 segments)
        if (segments.Length == 6 &&
            string.Equals(segments[2], "_git", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(segments[4], "pullrequest", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(segments[5], NumberStyles.None, CultureInfo.InvariantCulture, out var adoNumber))
        {
            var canonical = $"https://{host}/{segments[0]}/{segments[1]}/_git/{segments[3]}/pullrequest/{adoNumber}";
            return new PrUrlComponents(canonical, PrPlatform.AzureDevOps, host,
                Owner: segments[0], Project: segments[1], Repo: segments[3], Number: adoNumber);
        }

        throw new FormatException(
            $"'{raw}' is not a recognized PR URL shape. " +
            "Expected GitHub <https://host/owner/repo/pull/N> or " +
            "Azure DevOps <https://dev.azure.com/org/project/_git/repo/pullrequest/N>.");
    }
}

/// <summary>
/// Parsed components of a PR URL.
/// </summary>
/// <param name="Canonical">The canonical URL string.</param>
/// <param name="Platform">Which platform this URL is from.</param>
/// <param name="Host">Lowercased host (e.g. <c>github.com</c>, <c>microsoft.ghe.com</c>,
///   <c>dev.azure.com</c>).</param>
/// <param name="Owner">GitHub owner / GHE owner / ADO org name.</param>
/// <param name="Project">ADO project name; <c>null</c> for GitHub and GHE.</param>
/// <param name="Repo">Repository (or ADO repo) name.</param>
/// <param name="Number">PR number.</param>
public sealed record PrUrlComponents(
    string Canonical,
    PrPlatform Platform,
    string Host,
    string Owner,
    string? Project,
    string Repo,
    int Number);

/// <summary>
/// The platform a parsed PR URL belongs to.
/// </summary>
public enum PrPlatform
{
    GitHub,
    GitHubEnterprise,
    AzureDevOps,
}
