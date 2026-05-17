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
    /// Adds a GitHub-flavored source. <paramref name="kind"/> must be
    /// <see cref="SourceConfigKind.GitHub"/> or
    /// <see cref="SourceConfigKind.GitHubEnterprise"/>. If a source with the
    /// same id already exists, no-ops and returns false.
    /// </summary>
    Task<bool> AddGitHubSourceAsync(SourceConfigKind kind, string host, string? id = null, CancellationToken ct = default);

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
    /// Runs the same auth/identity checks the CLI <c>config doctor</c>
    /// runs and returns a structured report. Shell-outs to <c>gh</c> and
    /// the Azure CLI happen inside; expect a few hundred ms per source.
    /// </summary>
    Task<DoctorReport> RunDoctorAsync(CancellationToken ct = default);
}
