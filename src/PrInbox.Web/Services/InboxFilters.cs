using System.Collections.Frozen;
using System.Text.Json;
using System.Text.RegularExpressions;
using PrInbox.Core.Config;
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
    bool ShowDone,
    bool OnlyFlagged,
    FrozenSet<string> EnabledSources,
    FrozenSet<string> ExcludedRepos,
    FrozenSet<string> ExcludedAuthors,
    IReadOnlyList<Regex> IgnoredRepoRegexes,
    FrozenDictionary<string, string> SourceClassByConfigId,
    bool ShowOutOfScope = false,
    FrozenDictionary<string, IReadOnlyList<Regex>>? PathScopeByRepo = null)
{
    /// <summary>The four <c>src-*</c> classes the UI surfaces as chips.</summary>
    public static readonly FrozenSet<string> KnownSourceClasses =
        new[] { "src-emu", "src-public", "src-ghe", "src-ado" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bucket key for rows with a null/empty author login.</summary>
    public const string UnknownAuthorKey = "(unknown)";

    private const string PrefShowClosed = "inbox.show_closed";
    private const string PrefShowIgnored = "inbox.show_ignored";
    private const string PrefShowDone = "inbox.show_done";
    private const string PrefOnlyFlagged = "inbox.only_flagged";
    private const string PrefSourceFilter = "inbox.source_filter";
    private const string PrefExcludedRepos = "inbox.excluded_repos";
    private const string PrefExcludedAuthors = "inbox.excluded_authors";
    private const string PrefShowOutOfScope = "inbox.show_out_of_scope";

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
        var showDone = await prefs.GetBoolAsync(PrefShowDone, defaultValue: false, ct);
        var onlyFlagged = await prefs.GetBoolAsync(PrefOnlyFlagged, defaultValue: false, ct);
        var showOutOfScope = await prefs.GetBoolAsync(PrefShowOutOfScope, defaultValue: false, ct);

        var enabledSources = ParseJsonStringSet(await prefs.GetAsync(PrefSourceFilter, null, ct))
                             ?? KnownSourceClasses;
        var excludedRepos = ParseJsonStringSet(await prefs.GetAsync(PrefExcludedRepos, null, ct))
                            ?? FrozenSet<string>.Empty;
        var excludedAuthors = ParseJsonStringSet(await prefs.GetAsync(PrefExcludedAuthors, null, ct))
                              ?? FrozenSet<string>.Empty;

        var regexes = CompileIgnoredRepoRegexes(config.IgnoredRepos);
        var pathScopes = RepoPathScope.CompileRepoScopes(config.RepoPathFilters);

        return new InboxFilters(
            showClosed,
            showIgnored,
            showDone,
            onlyFlagged,
            enabledSources,
            excludedRepos,
            excludedAuthors,
            regexes,
            BuildSourceClassMap(config.Sources),
            showOutOfScope,
            pathScopes);
    }

    /// <summary>
    /// Build a snapshot from already-materialised in-memory state. Used by
    /// <c>Inbox.razor</c> where the local fields are kept in sync with the
    /// popovers and we don't want a per-render DB hit.
    /// </summary>
    public static InboxFilters From(
        bool showClosed,
        bool showIgnored,
        bool showDone,
        bool onlyFlagged,
        IEnumerable<string> enabledSources,
        IEnumerable<string> excludedRepos,
        IEnumerable<string> excludedAuthors,
        IReadOnlyList<Regex> ignoredRepoRegexes,
        IEnumerable<SourceConfig>? sourceConfigs = null,
        bool showOutOfScope = false,
        FrozenDictionary<string, IReadOnlyList<Regex>>? pathScopeByRepo = null)
        => new(
            showClosed,
            showIgnored,
            showDone,
            onlyFlagged,
            enabledSources.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            excludedRepos.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            excludedAuthors.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            ignoredRepoRegexes,
            BuildSourceClassMap(sourceConfigs),
            showOutOfScope,
            pathScopeByRepo);

    /// <summary>
    /// Back-compat overload: predates the <c>OnlyFlagged</c> filter.
    /// Defaults <c>onlyFlagged</c> to <c>false</c> so existing call sites
    /// behave identically.
    /// </summary>
    public static InboxFilters From(
        bool showClosed,
        bool showIgnored,
        bool showDone,
        IEnumerable<string> enabledSources,
        IEnumerable<string> excludedRepos,
        IEnumerable<string> excludedAuthors,
        IReadOnlyList<Regex> ignoredRepoRegexes,
        IEnumerable<SourceConfig>? sourceConfigs = null)
        => From(showClosed, showIgnored, showDone, onlyFlagged: false,
                enabledSources, excludedRepos, excludedAuthors,
                ignoredRepoRegexes, sourceConfigs);

    /// <summary>
    /// Back-compat overload for callers (and tests) written before the
    /// "marked done" filter existed. Defaults <c>showDone</c> to true so
    /// no test or sync-side caller silently loses rows that would have
    /// been visible under the older contract.
    /// </summary>
    public static InboxFilters From(
        bool showClosed,
        bool showIgnored,
        IEnumerable<string> enabledSources,
        IEnumerable<string> excludedRepos,
        IEnumerable<string> excludedAuthors,
        IReadOnlyList<Regex> ignoredRepoRegexes,
        IEnumerable<SourceConfig>? sourceConfigs = null)
        => From(showClosed, showIgnored, showDone: true, onlyFlagged: false,
                enabledSources, excludedRepos, excludedAuthors,
                ignoredRepoRegexes, sourceConfigs);

    /// <summary>Visible on the dashboard right now? (Sync-side overload.)
    /// <para>
    /// Sync-side callers don't have <see cref="InboxRow.IsMarkedDone"/>
    /// (which requires the latest snapshot's HeadSha) — they pass
    /// <paramref name="isMarkedDone"/> directly when they can, or rely on
    /// the default <c>false</c> when they can't. The done filter exists to
    /// keep the *UI* dashboard clean; sync-side prioritization is fine
    /// counting done rows as visible.
    /// </para>
    /// </summary>
    public bool ShouldShow(PullRequestRow row, bool isMarkedDone = false) => ShouldShowCore(
        row.Status, row.SourceId, row.DisplayRepo, row.AuthorLogin,
        row.IsIgnored, isMarkedDone, row.FlaggedAt.HasValue);

    /// <summary>Visible on the dashboard right now? (UI-side overload.)
    /// <para>
    /// This is the ONLY overload that applies monorepo path scoping —
    /// it has the row's changed-file signal (<see cref="InboxRow.TouchedPaths"/>
    /// / <see cref="InboxRow.TouchedPathState"/>). The sync-side
    /// <see cref="ShouldShow(PullRequestRow, bool)"/> deliberately does
    /// NOT, because sync candidates have no file data yet and hiding them
    /// would deprioritize their enrichment — they'd never get files and
    /// would stay hidden forever (enrichment deadlock).
    /// </para>
    /// </summary>
    public bool ShouldShow(InboxRow row) => ShouldShowCore(
        row.Status, row.SourceId, row.DisplayRepo, row.AuthorLogin,
        row.IsIgnored, row.IsMarkedDone, row.IsFlagged,
        row.TouchedPaths, row.TouchedPathState, applyPathScope: true);

    /// <summary>
    /// Map a SourceId to the UI chip class. Exposed because the chip
    /// classes are also persisted (the <c>source_filter</c> pref stores
    /// chip-class strings, not raw SourceIds) — both halves need to agree.
    /// <para>
    /// Used as a FALLBACK only — production callers should prefer
    /// <see cref="ClassifyConfig(SourceConfig)"/> via
    /// <see cref="SourceClassByConfigId"/>, which classifies from the
    /// authoritative (kind, host, identity) tuple. This method is the
    /// last-resort path for rows whose SourceId is not present in the
    /// current config (e.g. rows surviving a deleted source, or test
    /// fixtures that bypass the config).
    /// </para>
    /// <para>
    /// History note: older configs (and the existing test fixtures) used
    /// the two-part ids <c>gh.com:emu</c> / <c>gh.com:public</c>. The
    /// Settings UI's "+ Add GitHub.com" button writes the bare id
    /// <c>gh.com</c>. Both forms — and any future identity-suffixed form
    /// other than <c>:emu</c> — are treated as public.
    /// </para>
    /// </summary>
    public static string SourceClassOf(string sourceId) => sourceId switch
    {
        "gh.com:emu"    => "src-emu",
        "gh.com"        => "src-public",
        "gh.com:public" => "src-public",
        var id when id.StartsWith("gh.com:", StringComparison.Ordinal) => "src-public",
        var id when id.StartsWith("ghe.", StringComparison.Ordinal) => "src-ghe",
        var id when id.StartsWith("ado:", StringComparison.Ordinal) => "src-ado",
        _ => "src-other",
    };

    /// <summary>
    /// Authoritative chip classifier: derives the UI chip class from a
    /// configured source's (Kind, Host, Identity) tuple.
    /// <list type="bullet">
    ///   <item><see cref="SourceConfigKind.AzureDevOps"/> → <c>src-ado</c></item>
    ///   <item><see cref="SourceConfigKind.GitHubEnterprise"/> → <c>src-ghe</c></item>
    ///   <item><see cref="SourceConfigKind.GitHub"/> + EMU-shaped identity → <c>src-emu</c></item>
    ///   <item><see cref="SourceConfigKind.GitHub"/> + default/personal identity → <c>src-public</c></item>
    /// </list>
    /// EMU detection is based on the public GitHub EMU login convention
    /// <c>&lt;personal&gt;_&lt;org&gt;</c>: an identity that contains an
    /// underscore and is not the literal placeholder <c>default</c>. The
    /// Settings UI is expected to fill <c>Identity</c> from the
    /// authenticated <c>gh auth status</c> login when chunk 2 of the
    /// multi-identity work lands; until then most GitHub sources will
    /// still carry the literal <c>default</c> and therefore classify as
    /// public — which matches today's reality.
    /// </summary>
    public static string ClassifyConfig(SourceConfig sc) => sc.Kind switch
    {
        SourceConfigKind.AzureDevOps      => "src-ado",
        SourceConfigKind.GitHubEnterprise => "src-ghe",
        SourceConfigKind.GitHub when IsEmuIdentity(sc.Identity) => "src-emu",
        SourceConfigKind.GitHub           => "src-public",
        _                                  => "src-other",
    };

    /// <summary>
    /// Human-readable badge text for the chip class returned by
    /// <see cref="ClassifyConfig"/> / <see cref="SourceClassOf"/>. Kept
    /// in lock-step with the classifier so the visible label can never
    /// disagree with the chip filter (the bug Jean-Marc hit on the new
    /// install: <c>gh.com:jmprieur_microsoft</c> was correctly filtered
    /// by the EMU chip but visually badged "public" because the legacy
    /// id-string label resolver only knew about <c>gh.com:emu</c>).
    /// <paramref name="fallback"/> is the source id, used verbatim when
    /// the class is unknown.
    /// </summary>
    public static string LabelForClass(string chipClass, string fallback) => chipClass switch
    {
        "src-emu"    => "EMU",
        "src-public" => "public",
        "src-ghe"    => "proxima",
        "src-ado"    => "ado",
        _ => fallback,
    };

    private static bool IsEmuIdentity(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return false;
        if (string.Equals(identity, "default", StringComparison.OrdinalIgnoreCase)) return false;
        // EMU login convention: <personal>_<org>. The underscore is the
        // signal. Personal logins without underscores → public; explicit
        // identities like "jmprieur_microsoft" → EMU.
        return identity.Contains('_');
    }

    private static FrozenDictionary<string, string> BuildSourceClassMap(
        IEnumerable<SourceConfig>? sources)
    {
        if (sources is null) return FrozenDictionary<string, string>.Empty;
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sc in sources)
        {
            if (string.IsNullOrWhiteSpace(sc.Id)) continue;
            pairs[sc.Id] = ClassifyConfig(sc);
        }
        return pairs.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Classifier for a single row: prefers the config-derived class
    /// (which knows about identity) and falls back to the legacy
    /// id-string parser when the row references a source that's no
    /// longer in the current config.
    /// </summary>
    public string SourceClassFor(string sourceId) =>
        SourceClassByConfigId.TryGetValue(sourceId, out var cls)
            ? cls
            : SourceClassOf(sourceId);

    /// <summary>Author-filter key (null/empty → <see cref="UnknownAuthorKey"/>).</summary>
    public static string AuthorKeyOf(string? authorLogin) =>
        string.IsNullOrWhiteSpace(authorLogin) ? UnknownAuthorKey : authorLogin!;

    private bool ShouldShowCore(
        PullRequestStatus status,
        string sourceId,
        string displayRepo,
        string? authorLogin,
        bool isIgnored,
        bool isMarkedDone = false,
        bool isFlagged = false,
        IReadOnlyList<string>? touchedPaths = null,
        TouchedPathState touchedState = TouchedPathState.Unknown,
        bool applyPathScope = false)
    {
        // 1. Closed unless "Show closed".
        if (!ShowClosed && status != PullRequestStatus.Open) return false;

        // 2. Source chips. If every known chip is enabled, skip the filter
        //    so rows from new/unknown sources stay visible by default. This
        //    mirrors the Razor behavior — when the chip set is "full", we
        //    don't enforce an allow-list.
        //    Important: only enforce the chip filter for source classes the
        //    UI actually surfaces (KnownSourceClasses). A row whose class
        //    falls through to "src-other" — which can't be toggled because
        //    no chip exists for it — must never be hidden by unchecking an
        //    unrelated chip. Otherwise users see "uncheck ANY chip → all
        //    rows vanish" the moment they have a source the UI doesn't
        //    know about.
        if (EnabledSources.Count < KnownSourceClasses.Count)
        {
            var cls = SourceClassFor(sourceId);
            if (KnownSourceClasses.Contains(cls) && !EnabledSources.Contains(cls))
            {
                return false;
            }
        }

        // 3. Per-repo denylist.
        if (ExcludedRepos.Count > 0 && ExcludedRepos.Contains(displayRepo)) return false;

        // 4. Per-author denylist (with null-safe bucketing).
        if (ExcludedAuthors.Count > 0 && ExcludedAuthors.Contains(AuthorKeyOf(authorLogin))) return false;

        // 5. Monorepo path scope (DASHBOARD-ONLY — see ShouldShow(InboxRow)).
        //    A repo with a configured path filter only shows PRs that touch
        //    one of its folders. Fail-open by construction: a row is hidden
        //    ONLY when its changed-file list is Complete AND matches none of
        //    the repo's globs. Unknown (not enriched), Unavailable (ADO /
        //    fetch failed) and Truncated (huge PR past the file cap) all
        //    show. The "Show out-of-scope" toggle reveals the hidden set.
        if (applyPathScope && !ShowOutOfScope && PathScopeByRepo is { Count: > 0 } scopes)
        {
            var key = RepoPathScope.NormalizeRepoKey(displayRepo);
            if (scopes.TryGetValue(key, out var globs)
                && touchedState == TouchedPathState.Complete
                && touchedPaths is { Count: > 0 }
                && !RepoPathScope.IsInScope(touchedPaths, globs))
            {
                return false;
            }
        }

        // 6. Ignored (per-PR flag + config regex list).
        // Note: `disappeared_at != null` does NOT hide the row — disappeared
        // PRs surface in the main inbox with a "no longer assigned" chip so
        // the user can still see and act on them if they want to. Explicit
        // ignore (per-PR or regex) is the only way to hide them.
        if (!ShowIgnored)
        {
            if (isIgnored) return false;
            if (MatchesIgnoredRepoRegex(displayRepo)) return false;
        }

        // 7. Marked done (per-PR snooze). Same shape as Ignored: the
        //    "Show done" toggle reveals them; otherwise they're hidden.
        //    A row stops being "marked done" automatically once the
        //    author pushes a new commit (see InboxRow.IsMarkedDone).
        if (!ShowDone && isMarkedDone) return false;

        // 8. "Show only flagged" — a positive restrict, applied last so
        //    the other filters still apply (e.g. Closed PRs are still
        //    hidden unless Show closed is on, even when OnlyFlagged is
        //    on). This is a deliberate orthogonality choice: flagging
        //    doesn't bypass anything, it just lets you isolate the
        //    flagged subset of whatever is otherwise visible.
        if (OnlyFlagged && !isFlagged) return false;

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
