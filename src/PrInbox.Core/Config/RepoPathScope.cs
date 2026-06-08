using System.Collections.Frozen;
using System.Text;
using System.Text.RegularExpressions;

namespace PrInbox.Core.Config;

/// <summary>
/// Normalization + glob-matching helpers for the per-repo "only consider
/// PRs that touch these folders" filter (monorepo scoping).
/// <para>
/// Single source of truth shared by the inbox filter pipeline and the
/// Settings page so key/path/pattern handling can never disagree.
/// Deliberately a <b>tiny</b> glob dialect (compiled to anchored regex):
/// <list type="bullet">
///   <item><c>*</c> — any run of characters within a path segment (not <c>/</c>).</item>
///   <item><c>**</c> — any run of characters including <c>/</c>.</item>
///   <item>A bare prefix like <c>src/ServiceA</c> matches that exact path
///         <em>and</em> everything under it (<c>src/ServiceA/**</c>).</item>
///   <item>A trailing <c>/</c> or <c>/**</c> means "everything under this folder".</item>
/// </list>
/// </para>
/// <para>
/// Matching is <b>case-sensitive</b> for paths (GitHub paths are
/// case-sensitive) but repo keys are compared case-insensitively (repo
/// owners/names are not case-significant for lookup).
/// </para>
/// </summary>
public static class RepoPathScope
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Normalize a repo key for case-insensitive lookup: trim, switch
    /// back-slashes to forward, strip surrounding slashes.
    /// </summary>
    public static string NormalizeRepoKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        return raw.Trim().Replace('\\', '/').Trim('/');
    }

    /// <summary>
    /// Normalize a changed-file path: trim, switch back-slashes to forward,
    /// strip leading slashes. Casing is preserved.
    /// </summary>
    public static string NormalizePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var p = raw.Trim().Replace('\\', '/');
        return p.TrimStart('/');
    }

    /// <summary>
    /// Compile one repo's glob patterns into anchored regexes. A single
    /// user pattern can expand to two regexes (bare-prefix → exact + subtree).
    /// Patterns that are empty/whitespace are skipped. Patterns that fail to
    /// compile are dropped (defensive — never throws).
    /// </summary>
    public static IReadOnlyList<Regex> CompilePatterns(IEnumerable<string>? patterns)
    {
        var result = new List<Regex>();
        if (patterns is null) return result;
        foreach (var raw in patterns)
        {
            foreach (var rx in ExpandToRegexes(raw))
            {
                result.Add(rx);
            }
        }
        return result;
    }

    /// <summary>
    /// Compile a whole <c>repo → patterns</c> config into a per-repo regex
    /// map keyed by normalized repo (case-insensitive). Repos whose pattern
    /// list is empty/whitespace are treated as <b>unconfigured</b> and
    /// omitted, so they impose no filter (show all).
    /// </summary>
    public static FrozenDictionary<string, IReadOnlyList<Regex>> CompileRepoScopes(
        IReadOnlyDictionary<string, List<string>>? config)
    {
        if (config is null || config.Count == 0)
        {
            return FrozenDictionary<string, IReadOnlyList<Regex>>.Empty;
        }

        var map = new Dictionary<string, IReadOnlyList<Regex>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (repo, patterns) in config)
        {
            var key = NormalizeRepoKey(repo);
            if (key.Length == 0) continue;
            var globs = CompilePatterns(patterns);
            if (globs.Count == 0) continue; // empty list = unconfigured = show all
            map[key] = globs;
        }
        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when any of <paramref name="paths"/> matches any of
    /// <paramref name="globs"/>. Paths are normalized here so callers can
    /// pass raw changed-file paths.
    /// </summary>
    public static bool IsInScope(IEnumerable<string> paths, IReadOnlyList<Regex> globs)
    {
        if (globs.Count == 0) return false;
        foreach (var raw in paths)
        {
            var path = NormalizePath(raw);
            if (path.Length == 0) continue;
            foreach (var g in globs)
            {
                try
                {
                    if (g.IsMatch(path)) return true;
                }
                catch (RegexMatchTimeoutException)
                {
                    // Defensive: a pathological pattern shouldn't stall the
                    // dashboard. Treat as non-match for this glob.
                }
            }
        }
        return false;
    }

    private static IEnumerable<Regex> ExpandToRegexes(string? raw)
    {
        var p = NormalizePath(raw);
        if (p.Length == 0) yield break;

        var subtreeOnly = false;
        if (p.EndsWith("/**", StringComparison.Ordinal))
        {
            p = p[..^3];
            subtreeOnly = true;
        }
        else if (p.EndsWith("/", StringComparison.Ordinal))
        {
            p = p.TrimEnd('/');
            subtreeOnly = true;
        }
        if (p.Length == 0) yield break;

        var body = GlobToRegex(p);

        if (subtreeOnly)
        {
            yield return Compile("^" + body + "/.*$");
            yield break;
        }

        if (!p.Contains('*'))
        {
            // Bare prefix: match the exact path AND its subtree.
            yield return Compile("^" + body + "$");
            yield return Compile("^" + body + "/.*$");
            yield break;
        }

        yield return Compile("^" + body + "$");
    }

    private static string GlobToRegex(string glob)
    {
        var sb = new StringBuilder(glob.Length * 2);
        var i = 0;
        while (i < glob.Length)
        {
            var c = glob[i];
            if (c == '*')
            {
                var isDouble = i + 1 < glob.Length && glob[i + 1] == '*';
                if (isDouble)
                {
                    sb.Append(".*");
                    i += 2;
                }
                else
                {
                    sb.Append("[^/]*");
                    i += 1;
                }
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
                i += 1;
            }
        }
        return sb.ToString();
    }

    private static Regex Compile(string pattern) =>
        new(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant, MatchTimeout);
}
