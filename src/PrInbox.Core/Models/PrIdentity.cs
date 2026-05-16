namespace PrInbox.Core.Models;

/// <summary>
/// Stable identifier for a pull request. Two related fields:
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Url"/> — the canonical PR URL (e.g.
/// <c>https://github.com/owner/repo/pull/42</c>). Used as the lookup key
/// everywhere in storage and on the wire. Produced by
/// <see cref="PrUrl.Canonicalize(string)"/>.
/// </para>
/// <para>
/// <see cref="Stable"/> — the platform-id-based durable form
/// (e.g. <c>gh.com:&lt;repoId&gt;#&lt;prId&gt;</c>). Survives repo / project
/// renames; used by the upsert as the conflict target.
/// </para>
/// <para>
/// Both fields are stored on every <c>pull_requests</c> row. The SQL
/// column historically named <c>pr_identity</c> holds <see cref="Url"/>.
/// </para>
/// </remarks>
public readonly record struct PrIdentity(string Url, string Stable)
{
    /// <summary>Throws <see cref="ArgumentException"/> if either field is null or empty.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Url))
        {
            throw new ArgumentException("PrIdentity.Url is required.", nameof(Url));
        }
        if (string.IsNullOrWhiteSpace(Stable))
        {
            throw new ArgumentException("PrIdentity.Stable is required.", nameof(Stable));
        }
    }

    /// <summary>
    /// Build a canonical GitHub.com PR URL, e.g.
    /// <c>https://github.com/owner/repo/pull/N</c>.
    /// </summary>
    public static string FormatGitHubUrl(string owner, string repo, int number)
        => $"https://github.com/{owner}/{repo}/pull/{number}";

    /// <summary>
    /// Build a canonical GHE PR URL, e.g.
    /// <c>https://&lt;host&gt;/owner/repo/pull/N</c>.
    /// </summary>
    public static string FormatGheUrl(string host, string owner, string repo, int number)
        => $"https://{host}/{owner}/{repo}/pull/{number}";

    /// <summary>
    /// Build a canonical ADO PR URL, e.g.
    /// <c>https://dev.azure.com/org/project/_git/repo/pullrequest/N</c>.
    /// </summary>
    public static string FormatAdoUrl(string org, string project, string repo, int number)
        => $"https://dev.azure.com/{org}/{project}/_git/{repo}/pullrequest/{number}";

    /// <summary>
    /// Build a GitHub stable identity, e.g.
    /// <c>gh.com:&lt;repoId&gt;#&lt;prId&gt;</c>.
    /// </summary>
    public static string FormatGitHubStable(long repoId, long prId)
        => $"gh.com:{repoId}#{prId}";

    /// <summary>
    /// Build a GHE stable identity, e.g.
    /// <c>ghe.&lt;host&gt;:&lt;repoId&gt;#&lt;prId&gt;</c>.
    /// </summary>
    public static string FormatGheStable(string host, long repoId, long prId)
        => $"ghe.{host}:{repoId}#{prId}";

    /// <summary>
    /// Build an ADO stable identity, e.g.
    /// <c>ado:org/&lt;projectGuid&gt;/&lt;repoGuid&gt;#N</c>.
    /// </summary>
    public static string FormatAdoStable(string org, Guid projectId, Guid repoId, int number)
        => $"ado:{org}/{projectId:D}/{repoId:D}#{number}";

    public override string ToString() => Url;
}
