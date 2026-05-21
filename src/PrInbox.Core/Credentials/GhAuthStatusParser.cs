using System.Text.RegularExpressions;

namespace PrInbox.Core.Credentials;

/// <summary>
/// Pure parser for the text emitted by <c>gh auth status --hostname X</c>.
/// Lives separate from the shell-out so unit tests can feed canned text.
/// </summary>
/// <remarks>
/// <para>
/// <c>gh</c> emits subtly different output between versions and between
/// single- vs multi-account hosts. We accept three styles:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     Multi-account (gh ~2.40+):
///     <c>✓ Logged in to github.com account jmprieur (keyring)</c>
///     followed within the next few lines by
///     <c>- Active account: true</c> (or <c>false</c>).
///     </description>
///   </item>
///   <item>
///     <description>
///     Single-account (older gh):
///     <c>✓ Logged in to github.com as jmprieur (oauth_token)</c>.
///     Treated as <c>IsActive = true</c> because no other account exists.
///     </description>
///   </item>
///   <item>
///     <description>
///     Hostname-filtered output: <c>--hostname github.com</c> may filter
///     to only the requested host, but if it doesn't we still extract
///     all <c>Logged in to &lt;host&gt; ...</c> matches and the caller
///     filters by host downstream.
///     </description>
///   </item>
/// </list>
/// <para>
/// We tolerate output going to stderr — callers concatenate stdout and
/// stderr before handing the text in here.
/// </para>
/// </remarks>
public static class GhAuthStatusParser
{
    // Matches both "Logged in to github.com account jmprieur" and
    // "Logged in to github.com as jmprieur".
    private static readonly Regex LoggedInLine = new(
        @"Logged in to (?<host>\S+) (?:account|as) (?<login>[^\s\(]+)",
        RegexOptions.Compiled);

    private static readonly Regex ActiveLine = new(
        @"Active account:\s*(?<value>true|false)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches "- Token scopes: 'gist', 'read:org', 'repo'". Quotes may
    // be straight ' or curly ’ depending on terminal; we accept either.
    private static readonly Regex ScopesLine = new(
        @"Token scopes:\s*(?<list>.+)$",
        RegexOptions.Compiled);
    private static readonly Regex SingleScope = new(
        @"['""\u2018\u2019\u201C\u201D]([^'""\u2018\u2019\u201C\u201D,]+)['""\u2018\u2019\u201C\u201D]",
        RegexOptions.Compiled);

    /// <summary>
    /// Parse the combined stdout+stderr of <c>gh auth status</c>. Returns
    /// every login it finds matching <paramref name="hostname"/>
    /// (case-insensitive). Deduplicates by login (case-insensitive).
    /// </summary>
    public static IReadOnlyList<GitHubAuthIdentity> Parse(string output, string hostname)
    {
        if (string.IsNullOrWhiteSpace(output)) return Array.Empty<GitHubAuthIdentity>();

        var lines = output.Replace("\r\n", "\n").Split('\n');
        var result = new List<GitHubAuthIdentity>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < lines.Length; i++)
        {
            var m = LoggedInLine.Match(lines[i]);
            if (!m.Success) continue;
            if (!string.Equals(m.Groups["host"].Value, hostname, StringComparison.OrdinalIgnoreCase)) continue;

            var login = m.Groups["login"].Value;
            if (string.IsNullOrWhiteSpace(login)) continue;
            if (!seen.Add(login)) continue;

            // Default isActive=true for older-style "Logged in ... as ..."
            // lines (single-account hosts). For newer-style "account"
            // lines, look ahead a few lines for the explicit marker.
            bool isActive = lines[i].Contains(" as ", StringComparison.Ordinal);
            IReadOnlyList<string> scopes = Array.Empty<string>();
            for (int j = i + 1; j < Math.Min(i + 12, lines.Length); j++)
            {
                // Stop looking if we hit the next account entry.
                if (LoggedInLine.IsMatch(lines[j])) break;
                var a = ActiveLine.Match(lines[j]);
                if (a.Success)
                {
                    isActive = string.Equals(a.Groups["value"].Value, "true", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                var s = ScopesLine.Match(lines[j]);
                if (s.Success)
                {
                    var found = new List<string>();
                    foreach (Match sm in SingleScope.Matches(s.Groups["list"].Value))
                    {
                        var scope = sm.Groups[1].Value.Trim();
                        if (!string.IsNullOrEmpty(scope)) found.Add(scope);
                    }
                    scopes = found;
                }
            }

            result.Add(new GitHubAuthIdentity(login, isActive) { Scopes = scopes });
        }

        return result;
    }
}
