namespace PrInbox.Core.Models;

/// <summary>
/// Stable identifier for a pull request across sources, with a display form
/// suitable for command-line use and a durable form keyed on platform IDs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Display identity</b> (<see cref="Display"/>) is the human-readable
/// form used on the command line and in joins: e.g.
/// <c>gh.com:agency-microsoft/playground#4248</c>.
/// </para>
/// <para>
/// <b>Stable identity</b> (<see cref="Stable"/>) is the durable form
/// keyed on platform-immutable IDs: numeric repo and PR IDs for GitHub,
/// project + repo GUIDs for Azure DevOps. This survives repo / project
/// renames.
/// </para>
/// <para>
/// Both forms are stored on every <c>pull_requests</c> row.
/// </para>
/// </remarks>
public readonly record struct PrIdentity(string Display, string Stable)
{
    /// <summary>Throws <see cref="ArgumentException"/> if either form is null or empty.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Display))
        {
            throw new ArgumentException("PrIdentity.Display is required.", nameof(Display));
        }
        if (string.IsNullOrWhiteSpace(Stable))
        {
            throw new ArgumentException("PrIdentity.Stable is required.", nameof(Stable));
        }
    }

    /// <summary>
    /// Build a GitHub.com display identity, e.g.
    /// <c>gh.com:owner/repo#N</c>.
    /// </summary>
    public static string FormatGitHubDisplay(string owner, string repo, int number)
        => $"gh.com:{owner}/{repo}#{number}";

    /// <summary>
    /// Build a GHE display identity, e.g.
    /// <c>ghe.&lt;host&gt;:owner/repo#N</c>.
    /// </summary>
    public static string FormatGheDisplay(string host, string owner, string repo, int number)
        => $"ghe.{host}:{owner}/{repo}#{number}";

    /// <summary>
    /// Build an ADO display identity, e.g.
    /// <c>ado:org/project/repo#N</c>.
    /// </summary>
    public static string FormatAdoDisplay(string org, string project, string repo, int number)
        => $"ado:{org}/{project}/{repo}#{number}";

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

    public override string ToString() => Display;
}
