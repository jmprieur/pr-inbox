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
    private readonly IConfigService _configSvc;
    private readonly PullRequestRepository _prs;
    private readonly PrInboxDb _db;
    private readonly IGitHubAuthDiscovery _ghDiscovery;

    public DoctorService(
        IConfigService configSvc,
        PullRequestRepository prs,
        PrInboxDb db,
        IGitHubAuthDiscovery ghDiscovery)
    {
        _configSvc = configSvc;
        _prs = prs;
        _db = db;
        _ghDiscovery = ghDiscovery;
    }

    public async Task<EnrichedDoctorReport> RunAsync(CancellationToken ct = default)
    {
        // Run the three independent queries concurrently — the auth
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

        var advisories = DetectAdvisories(baseReport, ghIdentities);

        return new EnrichedDoctorReport(baseReport, enriched, advisories);
    }

    /// <summary>
    /// Detects configuration patterns that pass the auth check but waste
    /// work or are likely user mistakes. Today: one pattern.
    /// </summary>
    private static IReadOnlyList<DoctorAdvisory> DetectAdvisories(
        DoctorReport baseReport,
        IReadOnlyList<GitHubAuthIdentity> ghIdentities)
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
                // One bind action per default source — clicking removes
                // the default and (since the explicit one for activeLogin
                // already exists) collapses to a clean per-identity setup.
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
}
