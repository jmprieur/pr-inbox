using System.Collections.Frozen;
using System.Text.Json;
using System.Text.RegularExpressions;
using PrInbox.Core.Credentials;
using PrInbox.Core.Models;
using PrInbox.Core.Storage;

namespace PrInbox.Web.Services;

/// <summary>
/// Single source of truth for the inbox filter pipeline. Built from the
/// persisted UI preferences plus the <see cref="PrInboxConfig.IgnoredRepos"/>
/// regex list, then asked <see cref="ShouldShow(PullRequestRow)"/> /
/// <see cref="ShouldShow(InboxRow)"/> per row.
/// <para>
/// Used by:
/// <list type="bullet">
///   <item><c>Inbox.razor.VisibleRows</c> — to filter the dashboard.</item>
///   <item><see cref="InboxSyncHostedService"/> — to prioritize enrich
///         passes (visible PRs first, hidden PRs second).</item>
/// </list>
/// </para>
/// <para>
/// Snapshot semantics: the record is immutable. A consumer that wants to
/// re-evaluate after a filter change builds a fresh instance — there is no
/// in-place mutation. This keeps the sync loop's "filter pinned for the
/// cycle" property explicit and makes the dashboard render deterministic.
/// </para>
/// </summary>
public sealed record InboxFilters(
    bool ShowClosed,
    bool ShowIgnored,
    FrozenSet<string> EnabledSources,
    FrozenSet<string> ExcludedRepos,
    FrozenSet<string> ExcludedAuthors,
    IReadOnlyList<Regex> IgnoredRepoRegexes)
{
    /// <summary>The four <c>src-*</c> classes the UI surfaces as chips.</summary>
    public static readonly FrozenSet<string> KnownSourceClasses =
        new[] { "src-emu", "src-public", "src-ghe", "src-ado" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bucket key for rows with a null/empty author login.</summary>
    public const string UnknownAuthorKey = "(unknown)";

    private const string PrefShowClosed = "inbox.show_closed";
    private const string PrefShowIgnored = "inbox.show_ignored";
    private const string PrefSourceFilter = "inbox.source_filter";
    private const string PrefExcludedRepos = "inbox.excluded_repos";
    private const string PrefExcludedAuthors = "inbox.excluded_authors";

    /// <summary>Match-time cap for an ignored-repo regex. Anything slower is
    /// almost certainly catastrophic backtracking — treated as a non-match.</summary>
    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Build a snapshot from persisted prefs + the live config. Safe to
    /// call from any thread; reads only.
    /// </summary>
    public static async Task<InboxFilters> LoadAsync(
        UiPreferencesRepository prefs,
        PrInboxConfig config,
        CancellationToken ct = default)
    {
        var showClosed = await prefs.GetBoolAsync(PrefShowClosed, defaultValue: false, ct);
        var showIgnored = await prefs.GetBoolAsync(PrefShowIgnored, defaultValue: false, ct);

        var enabledSources = ParseJsonStringSet(await prefs.GetAsync(PrefSourceFilter, null, ct))
                             ?? KnownSourceClasses;
        var excludedRepos = ParseJsonStringSet(await prefs.GetAsync(PrefExcludedRepos, null, ct))
                            ?? FrozenSet<string>.Empty;
        var excludedAuthors = ParseJsonStringSet(await prefs.GetAsync(PrefExcludedAuthors, null, ct))
                              ?? FrozenSet<string>.Empty;

        var regexes = CompileIgnoredRepoRegexes(config.IgnoredRepos);

        return new InboxFilters(
            showClosed,
            showIgnored,
            enabledSources,
            excludedRepos,
            excludedAuthors,
            regexes);
    }

    /// <summary>
    /// Build a snapshot from already-materialised in-memory state. Used by
    /// <c>Inbox.razor</c> where the local fields are kept in sync with the
    /// popovers and we don't want a per-render DB hit.
    /// </summary>
    public static InboxFilters From(
        bool showClosed,
        bool showIgnored,
        IEnumerable<string> enabledSources,
        IEnumerable<string> excludedRepos,
        IEnumerable<string> excludedAuthors,
        IReadOnlyList<Regex> ignoredRepoRegexes)
        => new(
            showClosed,
            showIgnored,
            enabledSources.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            excludedRepos.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            excludedAuthors.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            ignoredRepoRegexes);

    /// <summary>Visible on the dashboard right now? (Sync-side overload.)</summary>
    public bool ShouldShow(PullRequestRow row) => ShouldShowCore(
        row.Status, row.SourceId, row.DisplayRepo, row.AuthorLogin,
        row.IsIgnored, row.DisappearedAt);

    /// <summary>Visible on the dashboard right now? (UI-side overload.)</summary>
    public bool ShouldShow(InboxRow row) => ShouldShowCore(
        row.Status, row.SourceId, row.DisplayRepo, row.AuthorLogin,
        row.IsIgnored, row.DisappearedAt);

    /// <summary>
    /// Map a SourceId to the UI chip class. Exposed because the chip
    /// classes are also persisted (the <c>source_filter</c> pref stores
    /// chip-class strings, not raw SourceIds) — both halves need to agree.
    /// </summary>
    public static string SourceClassOf(string sourceId) => sourceId switch
    {
        "gh.com:emu"    => "src-emu",
        "gh.com:public" => "src-public",
        var id when id.StartsWith("ghe.", StringComparison.Ordinal) => "src-ghe",
        var id when id.StartsWith("ado:", StringComparison.Ordinal) => "src-ado",
        _ => "src-other",
    };

    /// <summary>Author-filter key (null/empty → <see cref="UnknownAuthorKey"/>).</summary>
    public static string AuthorKeyOf(string? authorLogin) =>
        string.IsNullOrWhiteSpace(authorLogin) ? UnknownAuthorKey : authorLogin!;

    private bool ShouldShowCore(
        PullRequestStatus status,
        string sourceId,
        string displayRepo,
        string? authorLogin,
        bool isIgnored,
        DateTimeOffset? disappearedAt)
    {
        // 1. Closed unless "Show closed".
        if (!ShowClosed && status != PullRequestStatus.Open) return false;

        // 2. Source chips. If every known chip is enabled, skip the filter
        //    so rows from new/unknown sources stay visible by default. This
        //    mirrors the Razor behavior — when the chip set is "full", we
        //    don't enforce an allow-list.
        if (EnabledSources.Count < KnownSourceClasses.Count
            && !EnabledSources.Contains(SourceClassOf(sourceId)))
        {
            return false;
        }

        // 3. Per-repo denylist.
        if (ExcludedRepos.Count > 0 && ExcludedRepos.Contains(displayRepo)) return false;

        // 4. Per-author denylist (with null-safe bucketing).
        if (ExcludedAuthors.Count > 0 && ExcludedAuthors.Contains(AuthorKeyOf(authorLogin))) return false;

        // 5. Ignored / disappeared (per-PR flag + config regex list).
        if (!ShowIgnored)
        {
            if (isIgnored) return false;
            if (disappearedAt is not null && status == PullRequestStatus.Open) return false;
            if (MatchesIgnoredRepoRegex(displayRepo)) return false;
        }

        return true;
    }

    private bool MatchesIgnoredRepoRegex(string displayRepo)
    {
        if (IgnoredRepoRegexes.Count == 0) return false;
        foreach (var re in IgnoredRepoRegexes)
        {
            try
            {
                if (re.IsMatch(displayRepo)) return true;
            }
            catch (RegexMatchTimeoutException)
            {
                // Defensive only: a runaway pattern shouldn't stall sync
                // prioritization. Treat as non-match; caller will see the
                // row as visible — safer than silently hiding things.
            }
            catch
            {
                // Same rationale for any other runtime regex error.
            }
        }
        return false;
    }

    /// <summary>
    /// Compile patterns with a match timeout and ignore the ones that
    /// won't compile. Mirrors <c>Inbox.razor.CompileIgnoredRepoRegexes</c>.
    /// </summary>
    public static IReadOnlyList<Regex> CompileIgnoredRepoRegexes(IEnumerable<string>? patterns)
    {
        if (patterns is null) return Array.Empty<Regex>();
        var list = new List<Regex>();
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            try
            {
                list.Add(new Regex(
                    pattern,
                    RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                    RegexMatchTimeout));
            }
            catch
            {
                // Bad pattern. Drop it — same policy as the Razor side.
            }
        }
        return list;
    }

    private static FrozenSet<string>? ParseJsonStringSet(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var arr = JsonSerializer.Deserialize<string[]>(raw);
            if (arr is null || arr.Length == 0) return FrozenSet<string>.Empty;
            return arr.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return null;
        }
    }
}
