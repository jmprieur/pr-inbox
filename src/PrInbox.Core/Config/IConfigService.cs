using PrInbox.Core.Credentials;

namespace PrInbox.Core.Config;

/// <summary>
/// Read/write façade over <see cref="PrInboxConfig"/> for callers that
/// want a higher-level API than load → mutate → save. The Web UI's
/// Settings page is the primary consumer. Each mutation persists to
/// disk and refreshes the singleton injected at startup so live
/// consumers (e.g. <c>Inbox.razor</c>'s <see cref="PrInboxConfig.IgnoredRepos"/>
/// read) see the new values immediately.
/// </summary>
/// <remarks>
/// <para>
/// Source-list and ADO-project changes <em>are</em> picked up by the
/// background sync service on its next tick (it reloads from disk
/// every cycle). Per-source publisher wiring is built once at startup,
/// so adding a new source means publishers for that source won't be
/// available until restart. That's acceptable for v0.2 because
/// publishing only happens after a curated review, which is rare
/// relative to setup.
/// </para>
/// </remarks>
public interface IConfigService
{
    /// <summary>The path the service reads/writes (typically <see cref="PrInboxConfig.DefaultPath"/>).</summary>
    string ConfigPath { get; }

    /// <summary>Returns whether <see cref="ConfigPath"/> exists on disk.</summary>
    bool ConfigFileExists();

    /// <summary>Loads the current config fresh from disk (does not return the cached singleton).</summary>
    Task<PrInboxConfig> GetAsync(CancellationToken ct = default);

    /// <summary>
    /// Adds a GitHub-flavored source bound to <c>"default"</c> identity
    /// (uses whichever <c>gh</c> account is currently active for the
    /// host). <paramref name="kind"/> must be <see cref="SourceConfigKind.GitHub"/>
    /// or <see cref="SourceConfigKind.GitHubEnterprise"/>. If a source
    /// with the same id already exists, no-ops and returns false.
    /// </summary>
    Task<bool> AddGitHubSourceAsync(SourceConfigKind kind, string host, string? id = null, CancellationToken ct = default);

    /// <summary>
    /// Adds a GitHub-flavored source explicitly bound to a specific
    /// <c>gh</c> login. <paramref name="identity"/> is the GitHub login
    /// (e.g. <c>jmprieur_microsoft</c>); the token provider will pass
    /// <c>--user &lt;identity&gt;</c> to <c>gh auth token</c> so this
    /// source always uses that account regardless of which one is
    /// "active" in <c>gh</c>. Default id is <c>gh.&lt;host&gt;:&lt;login&gt;</c>
    /// (or <c>gh.com:&lt;login&gt;</c> for github.com). If a source with
    /// the same id already exists, no-ops and returns false.
    /// </summary>
    Task<bool> AddGitHubSourceWithIdentityAsync(
        SourceConfigKind kind,
        string host,
        string identity,
        string? id = null,
        CancellationToken ct = default);

    /// <summary>
    /// Adds an Azure DevOps (org, project) entry under
    /// <see cref="PrInboxConfig.Ado"/>. If the pair already exists, no-ops
    /// and returns false.
    /// </summary>
    Task<bool> AddAdoProjectAsync(string org, string project, CancellationToken ct = default);

    /// <summary>
    /// Removes the source with the given id. Returns true if removed.
    /// </summary>
    Task<bool> RemoveSourceAsync(string sourceId, CancellationToken ct = default);

    /// <summary>
    /// Removes the (org, project) entry. Returns true if removed.
    /// </summary>
    Task<bool> RemoveAdoProjectAsync(string org, string project, CancellationToken ct = default);

    /// <summary>
    /// Replaces the entire <see cref="PrInboxConfig.IgnoredRepos"/> list.
    /// Pass an empty list to clear.
    /// </summary>
    Task SetIgnoredReposAsync(IReadOnlyList<string> patterns, CancellationToken ct = default);

    /// <summary>
    /// Updates the two live-toggleable Review-launcher flags
    /// (<see cref="ReviewLauncherSettings.AutoSend"/> and
    /// <see cref="ReviewLauncherSettings.Yolo"/>) and mirrors them onto
    /// the DI singleton so the next review launch picks them up without
    /// a process restart.
    /// </summary>
    Task SetReviewLauncherFlagsAsync(bool autoSend, bool yolo, CancellationToken ct = default);

    /// <summary>
    /// Sets the Review-launcher tab colour
    /// (<see cref="ReviewLauncherSettings.TabColor"/>) and mirrors it onto
    /// the DI singleton so the next review launch picks it up without a
    /// process restart. The value is normalized via
    /// <see cref="ReviewLauncherSettings.NormalizeTabColor"/>: a valid
    /// <c>#rgb</c>/<c>#rrggbb</c> hex is stored as-is; anything else
    /// (including blank) is stored as empty, which disables colouring.
    /// </summary>
    Task SetReviewLauncherTabColorAsync(string tabColor, CancellationToken ct = default);

    /// <summary>
    /// Replaces the entire <see cref="PrInboxConfig.RepoPathFilters"/> map
    /// (monorepo path scoping) and mirrors it onto the DI singleton so the
    /// inbox picks it up without a restart. Repos with an empty pattern
    /// list are dropped (treated as unconfigured = show all).
    /// </summary>
    Task SetRepoPathFiltersAsync(
        IReadOnlyDictionary<string, IReadOnlyList<string>> filters,
        CancellationToken ct = default);

    /// <summary>
    /// Runs the same auth/identity checks the CLI <c>config doctor</c>
    /// runs and returns a structured report. Shell-outs to <c>gh</c> and
    /// the Azure CLI happen inside; expect a few hundred ms per source.
    /// </summary>
    Task<DoctorReport> RunDoctorAsync(CancellationToken ct = default);

    /// <summary>
    /// Migrates a default-identity GitHub source to an explicit identity
    /// in a single load → mutate → save cycle. Removes the source
    /// identified by <paramref name="sourceId"/> and adds a new
    /// explicit-identity source bound to <paramref name="identity"/>
    /// (using the same kind/host). If an explicit source for that
    /// (host, identity) pair already exists, the default source is just
    /// removed (the explicit one already does the work).
    /// </summary>
    /// <remarks>
    /// Atomic at the file level — either both mutations land or neither
    /// does. The new explicit source uses the same default id-derivation
    /// as <see cref="AddGitHubSourceWithIdentityAsync"/> (e.g.
    /// <c>gh.com:jenny_microsoft</c>), so subsequent sync runs are
    /// indistinguishable from having added it manually.
    /// </remarks>
    Task<BindIdentityResult> BindGitHubSourceToIdentityAsync(
        string sourceId,
        string identity,
        CancellationToken ct = default);
}

/// <summary>
/// Outcome of <see cref="IConfigService.BindGitHubSourceToIdentityAsync"/>.
/// </summary>
public enum BindIdentityResult
{
    /// <summary>Default source was removed and a new explicit-identity source was added.</summary>
    Migrated,
    /// <summary>Default source was removed; an explicit source for the target identity already existed and was left in place.</summary>
    RemovedDuplicate,
    /// <summary>No source with the given id exists.</summary>
    NotFound,
    /// <summary>Source exists but is not a default-identity GitHub source (wrong kind or already has an explicit identity).</summary>
    NotEligible,
}
