using PrInbox.Core.Config;
using PrInbox.Core.Credentials;
using PrInbox.Core.Models;
using PrInbox.Core.Storage;

namespace PrInbox.Web.Services;

/// <summary>
/// Web-layer enrichment around <see cref="IConfigService.RunDoctorAsync"/>.
/// The Core report covers auth (can we mint a token? what identity did
/// it resolve to?). This service layers on runtime signal pulled from
/// the local SQLite DB and live gh-CLI state:
///
///   * <b>Last successful sync per source</b> — proves the background
///     loop is actually using the source after auth succeeded.
///   * <b>Open-PR count per source</b> — proves the fetch path returns
///     something meaningful; "0" on a source you know has PRs is a red
///     flag a pure auth check can't surface.
///   * <b>EMU / active-login chips</b> — already in the gh discovery
///     output, just not bubbled into Doctor before. Useful at a glance
///     when you have multiple github.com identities bound.
///   * <b>Double-fetch advisory</b> — detects the
///     default-identity-source + explicit-identity-source-for-the-active-
///     gh-login pattern, which silently fetches the same PRs twice and
///     wastes a chunk of every sync cycle. We surface a warning row;
///     the one-click "remove the default-identity source" migration is
///     a separate piece of work.
///
/// Lives in PrInbox.Web (not PrInbox.Core) so the Core service stays
/// decoupled from storage; the CLI's <c>doctor</c> verb keeps using the
/// lighter Core report for portability.
/// </summary>
public sealed class DoctorService
{
    // Scopes we expect on every GitHub source's token. "repo" covers
    // PR reads on private repos; "read:org" lets the user enumerate org
    // teams (used by some queries). Missing either is a real headache.
    private static readonly IReadOnlyList<string> RequiredGhScopes = new[] { "repo", "read:org" };

    // Below this remaining/limit ratio we surface a rate-limit advisory.
    // GitHub gives 5000/hour for an authenticated user — 15% = 750 left.
    private const double RateLimitWarnFraction = 0.15;

    private readonly IConfigService _configSvc;
    private readonly PullRequestRepository _prs;
    private readonly PrInboxDb _db;
    private readonly IGitHubAuthDiscovery _ghDiscovery;
    private readonly IGitHubRateLimitProbe _rateLimitProbe;

    public DoctorService(
        IConfigService configSvc,
        PullRequestRepository prs,
        PrInboxDb db,
        IGitHubAuthDiscovery ghDiscovery,
        IGitHubRateLimitProbe rateLimitProbe)
    {
        _configSvc = configSvc;
        _prs = prs;
        _db = db;
        _ghDiscovery = ghDiscovery;
        _rateLimitProbe = rateLimitProbe;
    }

    public async Task<EnrichedDoctorReport> RunAsync(CancellationToken ct = default)
    {
        // Run the four independent queries concurrently — the auth
        // probe shells out to gh/az and is the slowest leg by far, so
        // overlapping it with the SQL queries pays off on every run.
        var baseReportTask = _configSvc.RunDoctorAsync(ct);
        var lastRunsTask = new SyncRunRepository(_db).GetLatestPerSourceAsync(ct);
        var openPrsTask = _prs.ListActiveAsync(ct);
        var ghIdentitiesTask = SafeListIdentitiesAsync("github.com", ct);

        await Task.WhenAll(baseReportTask, lastRunsTask, openPrsTask, ghIdentitiesTask);

        var baseReport = baseReportTask.Result;
        var lastRuns = lastRunsTask.Result;
        var openPrs = openPrsTask.Result;
        var ghIdentities = ghIdentitiesTask.Result;

        // Rate-limit probes are a second wave — they only make sense
        // for hosts where we know we have a working gh login. We dedupe
        // by host across all sources (each host has one shared bucket
        // regardless of identity).
        var ghHosts = baseReport.Sources
            .Where(s => (s.Kind == SourceConfigKind.GitHub || s.Kind == SourceConfigKind.GitHubEnterprise)
                        && s.Enabled
                        && s.Ok
                        && !string.IsNullOrWhiteSpace(s.Host))
            .Select(s => s.Host!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var rateLimitsTask = ProbeRateLimitsAsync(ghHosts, ct);
        var rateLimits = await rateLimitsTask;

        // Build a sourceId -> latest-sync-run map. GetLatestPerSourceAsync
        // returns one row per (source, identity) pair; we collapse to
        // per-source by picking the most recent run regardless of
        // identity. The Doctor table groups by source; per-identity
        // breakdown would need a different UI.
        var latestBySource = lastRuns
            .GroupBy(r => r.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.StartedAt).First(),
                          StringComparer.OrdinalIgnoreCase);

        var openCountsBySource = openPrs
            .GroupBy(p => p.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var ghByLogin = ghIdentities
            .ToDictionary(i => i.Login, StringComparer.OrdinalIgnoreCase);

        var enriched = new List<EnrichedSourceCheck>(baseReport.Sources.Count);
        foreach (var s in baseReport.Sources)
        {
            latestBySource.TryGetValue(s.Id, out var lastRun);
            openCountsBySource.TryGetValue(s.Id, out var openCount);

            bool? isEmu = null;
            var isActiveGhLogin = false;
            if (s.Identity is { } login &&
                !string.Equals(login, "default", StringComparison.OrdinalIgnoreCase) &&
                ghByLogin.TryGetValue(login, out var gh))
            {
                isEmu = gh.IsEmu;
                isActiveGhLogin = gh.IsActive;
            }

            enriched.Add(new EnrichedSourceCheck(
                Base: s,
                LastSyncAt: lastRun?.StartedAt,
                LastSyncStatus: lastRun?.Status,
                LastSyncError: lastRun?.Error,
                OpenPrCount: openCount,
                IsEmu: isEmu,
                IsActiveGhLogin: isActiveGhLogin));
        }

        var advisories = DetectAdvisories(baseReport, enriched, ghIdentities, ghByLogin, rateLimits);

        return new EnrichedDoctorReport(baseReport, enriched, advisories);
    }

    /// <summary>
    /// Detects configuration patterns that pass the auth check but waste
    /// work, are likely user mistakes, or are headed toward failure.
    /// </summary>
    private static IReadOnlyList<DoctorAdvisory> DetectAdvisories(
        DoctorReport baseReport,
        IReadOnlyList<EnrichedSourceCheck> enriched,
        IReadOnlyList<GitHubAuthIdentity> ghIdentities,
        IReadOnlyDictionary<string, GitHubAuthIdentity> ghByLogin,
        IReadOnlyDictionary<string, RateLimitSnapshot> rateLimits)
    {
        var advisories = new List<DoctorAdvisory>();

        // --- Double-fetch detection -----------------------------------
        // A "default-identity" gh.com source fetches PRs for whichever
        // gh login is currently active. If the user has ALSO added an
        // explicit-identity source bound to that same active login, the
        // same PRs are fetched twice every sync. This is almost always
        // unintentional — it usually happens when someone adopts the
        // multi-identity flow without realizing the legacy default
        // source is still there.
        var activeLogin = ghIdentities
            .FirstOrDefault(i => i.IsActive)?.Login;
        if (!string.IsNullOrEmpty(activeLogin))
        {
            var defaultGhSources = baseReport.Sources
                .Where(s => s.Kind == SourceConfigKind.GitHub
                            && string.Equals(s.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                            && string.Equals(s.Identity, "default", StringComparison.OrdinalIgnoreCase)
                            && s.Enabled)
                .ToList();
            var explicitMatch = baseReport.Sources
                .FirstOrDefault(s => s.Kind == SourceConfigKind.GitHub
                                     && string.Equals(s.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                                     && string.Equals(s.Identity, activeLogin, StringComparison.OrdinalIgnoreCase)
                                     && s.Enabled);

            if (defaultGhSources.Count > 0 && explicitMatch is not null)
            {
                var defaultIds = string.Join(", ", defaultGhSources.Select(s => $"`{s.Id}`"));
                var actions = defaultGhSources
                    .Select(s => new DoctorAdvisoryAction(
                        Kind: DoctorAdvisoryActionKind.BindToIdentity,
                        SourceId: s.Id,
                        TargetIdentity: activeLogin,
                        Label: $"Bind `{s.Id}` to `{activeLogin}`"))
                    .ToList();
                advisories.Add(new DoctorAdvisory(
                    Severity: DoctorAdvisorySeverity.Warning,
                    Title: "Double-fetch: default-identity source overlaps with active gh login",
                    Detail: $"You have {defaultIds} (default identity) AND `{explicitMatch.Id}` " +
                            $"explicitly bound to `{activeLogin}`, which is the currently active gh account. " +
                            $"Both fetch the same PRs every sync cycle.",
                    Suggestion: $"Remove {defaultIds} from Sources — the explicit `{explicitMatch.Id}` " +
                                $"covers the same identity and won't break if you `gh auth switch` later.",
                    Actions: actions));
            }
        }

        // --- Failed last sync -----------------------------------------
        // Auth probe might pass while the actual fetch fails (network
        // blip, transient 5xx, repo permissions change). One advisory
        // per failing source — most common is one, so we don't bother
        // collapsing.
        foreach (var r in enriched.Where(r => r.Base.Enabled
                                              && r.LastSyncStatus == SyncRunStatus.Failed))
        {
            var when = r.LastSyncAt is { } at
                ? at.ToLocalTime().ToString("HH:mm")
                : "an earlier sync";
            var errLine = string.IsNullOrWhiteSpace(r.LastSyncError)
                ? string.Empty
                : $" Error: {r.LastSyncError.Trim()}";
            advisories.Add(new DoctorAdvisory(
                Severity: DoctorAdvisorySeverity.Warning,
                Title: $"Last sync failed: `{r.Base.Id}`",
                Detail: $"The most recent sync attempt for `{r.Base.Id}` (at {when}) ended in failure.{errLine}",
                Suggestion: "Click Retry to kick a fresh sync; if it keeps failing, check Logs / network / token validity.",
                Actions: new[]
                {
                    new DoctorAdvisoryAction(
                        Kind: DoctorAdvisoryActionKind.RetrySync,
                        SourceId: r.Base.Id,
                        TargetIdentity: string.Empty,
                        Label: $"Retry sync for `{r.Base.Id}`"),
                }));
        }

        // --- Missing gh scopes ----------------------------------------
        // For each gh source whose identity we recognise, compare its
        // token scopes to the required set. Emit one advisory per
        // (host, login) with missing scopes (deduped — multiple sources
        // bound to the same login would otherwise generate duplicates).
        var seenScopeAdvisory = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in baseReport.Sources)
        {
            if (!s.Enabled) continue;
            if (s.Kind != SourceConfigKind.GitHub && s.Kind != SourceConfigKind.GitHubEnterprise) continue;
            if (string.IsNullOrWhiteSpace(s.Host)) continue;

            // Resolve which login this source effectively uses. Explicit
            // identity wins; default falls back to the currently-active
            // gh login on that host (only known for github.com — we don't
            // probe GHE in v1).
            string? effectiveLogin = null;
            if (!string.IsNullOrWhiteSpace(s.Identity)
                && !string.Equals(s.Identity, "default", StringComparison.OrdinalIgnoreCase))
            {
                effectiveLogin = s.Identity;
            }
            else if (string.Equals(s.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrEmpty(activeLogin))
            {
                effectiveLogin = activeLogin;
            }
            if (effectiveLogin is null) continue;

            if (!ghByLogin.TryGetValue(effectiveLogin, out var ident)) continue;
            // Empty scopes list = parser didn't see the line. Treat as
            // unknown rather than empty — better to under-warn than to
            // cry wolf on older gh versions that don't print scopes.
            if (ident.Scopes.Count == 0) continue;

            var missing = RequiredGhScopes
                .Where(req => !ident.Scopes.Any(have =>
                    string.Equals(have, req, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (missing.Count == 0) continue;

            var key = $"{s.Host}|{effectiveLogin}";
            if (!seenScopeAdvisory.Add(key)) continue;

            var missingList = string.Join(", ", missing.Select(m => $"`{m}`"));
            var cmd = $"gh auth refresh -h {s.Host} -s {string.Join(",", missing)}";
            advisories.Add(new DoctorAdvisory(
                Severity: DoctorAdvisorySeverity.Warning,
                Title: $"Missing token scopes for `{effectiveLogin}` on `{s.Host}`",
                Detail: $"The gh token for `{effectiveLogin}` is missing required scope(s): {missingList}. " +
                        $"PR queries may return partial or empty results.",
                Suggestion: $"Run this in a terminal (gh needs your device-code input): `{cmd}`"));
        }

        // --- Rate-limit headroom --------------------------------------
        // Below 15% remaining we warn. No one-click fix — you wait for
        // the window to reset. Surfacing the reset time turns "huh, my
        // inbox is empty" into "ah, I'm throttled until X:YZ".
        foreach (var kv in rateLimits)
        {
            var snap = kv.Value;
            if (snap.RemainingFraction >= RateLimitWarnFraction) continue;

            var resetLocal = snap.ResetAt.ToLocalTime().ToString("HH:mm");
            var minutes = Math.Max(0, (int)Math.Ceiling((snap.ResetAt - DateTimeOffset.UtcNow).TotalMinutes));
            advisories.Add(new DoctorAdvisory(
                Severity: DoctorAdvisorySeverity.Info,
                Title: $"Low API rate-limit headroom on `{kv.Key}`",
                Detail: $"{snap.Remaining}/{snap.Limit} requests left in this window. " +
                        $"Window resets at {resetLocal} (~{minutes} min).",
                Suggestion: "Sync cycles may slow or skip until the window resets. " +
                            "Consider reducing source count or widening the sync interval."));
        }

        return advisories;
    }

    private async Task<IReadOnlyList<GitHubAuthIdentity>> SafeListIdentitiesAsync(
        string host, CancellationToken ct)
    {
        try
        {
            return await _ghDiscovery.ListIdentitiesAsync(host, ct);
        }
        catch
        {
            // Discovery failures (gh not installed, no logins, timeout)
            // already collapse to empty list inside the discovery
            // implementation; this catch is a belt-and-suspenders so
            // any future contract change doesn't take Doctor down.
            return Array.Empty<GitHubAuthIdentity>();
        }
    }

    private async Task<IReadOnlyDictionary<string, RateLimitSnapshot>> ProbeRateLimitsAsync(
        IReadOnlyList<string> hosts, CancellationToken ct)
    {
        if (hosts.Count == 0)
        {
            return new Dictionary<string, RateLimitSnapshot>(StringComparer.OrdinalIgnoreCase);
        }

        var tasks = hosts.Select(async h =>
        {
            try { return (Host: h, Snap: await _rateLimitProbe.GetCoreAsync(h, ct)); }
            catch { return (Host: h, Snap: (RateLimitSnapshot?)null); }
        }).ToList();
        var results = await Task.WhenAll(tasks);

        var dict = new Dictionary<string, RateLimitSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var (host, snap) in results)
        {
            if (snap is not null) dict[host] = snap;
        }
        return dict;
    }
}

public sealed record EnrichedDoctorReport(
    DoctorReport Base,
    IReadOnlyList<EnrichedSourceCheck> Sources,
    IReadOnlyList<DoctorAdvisory> Advisories);

public sealed record EnrichedSourceCheck(
    SourceCheck Base,
    DateTimeOffset? LastSyncAt,
    SyncRunStatus? LastSyncStatus,
    string? LastSyncError,
    int OpenPrCount,
    bool? IsEmu,
    bool IsActiveGhLogin);

public enum DoctorAdvisorySeverity
{
    Info,
    Warning,
}

public sealed record DoctorAdvisory(
    DoctorAdvisorySeverity Severity,
    string Title,
    string Detail,
    string Suggestion,
    IReadOnlyList<DoctorAdvisoryAction>? Actions = null);

/// <summary>
/// Optional one-click remediation tied to an advisory. Each action is a
/// declarative "what to do" — the UI layer translates Kind into a
/// concrete <see cref="IConfigService"/> call.
/// </summary>
public sealed record DoctorAdvisoryAction(
    DoctorAdvisoryActionKind Kind,
    string SourceId,
    string TargetIdentity,
    string Label);

public enum DoctorAdvisoryActionKind
{
    /// <summary>Bind a default-identity GitHub source to a specific gh login (or remove it if a duplicate explicit already exists).</summary>
    BindToIdentity,
    /// <summary>Trigger an immediate sync (out-of-band, doesn't wait for next background tick).</summary>
    RetrySync,
}
