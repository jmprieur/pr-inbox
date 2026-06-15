using PrInbox.Core.Models;

namespace PrInbox.Sources;

/// <summary>
/// Read-only contract for a PR source platform.
/// </summary>
/// <remarks>
/// <para>
/// v0.1 source adapters implement this interface only. There is no write
/// interface in v0.1 — a future <c>IPrReviewPublisher</c> would be a
/// separate type, so v0.1 code cannot accidentally mutate platform state.
/// </para>
/// <para>
/// The contract is split into two progressive-fetch tiers:
/// <list type="bullet">
///   <item><see cref="ListAssignedFastAsync"/> — tier-2: cheap, one call per
///         source, streams PR-list entries as they become available.</item>
///   <item><see cref="EnrichAsync"/> — tier-3: one bundled call per PR
///         (detail + threads).</item>
/// </list>
/// </para>
/// <para>
/// Implementations are responsible for:
/// <list type="bullet">
///   <item>Token acquisition (via the credential delegation layer).</item>
///   <item>Rate-limit handling (exponential backoff on 429/503).</item>
///   <item>Translating platform-native shapes into <see cref="RemotePullRequest"/>
///         and friends, populating both display and stable identity fields.</item>
///   <item>Bot detection (set <c>IsBot</c> and <c>BotKind</c> on threads).</item>
/// </list>
/// </para>
/// </remarks>
public interface IPrReadSource
{
    /// <summary>
    /// Stable identifier for this source instance (e.g. <c>gh.com:emu</c>,
    /// <c>ghe.proxima</c>, <c>ado:mseng</c>).
    /// </summary>
    string SourceId { get; }

    /// <summary>
    /// What kind of source this is. Drives storage routing and brief generation.
    /// </summary>
    SourceKind Kind { get; }

    /// <summary>
    /// Capability flags for this source. Drive caller behavior without
    /// platform-specific conditionals.
    /// </summary>
    SourceCapabilities Capabilities { get; }

    /// <summary>
    /// Tier-2 fast listing: streams every PR where the authenticated user is
    /// currently a requested reviewer. Items yield as the source's search
    /// pages return; consumers may render the inbox incrementally.
    /// </summary>
    /// <remarks>
    /// For ADO (which has no global reviewer inbox), implementations enumerate
    /// the configured (org, project) pairs. The enumeration completes when
    /// all pages/projects have been exhausted.
    /// </remarks>
    IAsyncEnumerable<RemotePullRequest> ListAssignedFastAsync(CancellationToken ct);

    /// <summary>
    /// Tier-2 fast listing of PRs the authenticated user <em>authored</em>
    /// (GitHub's <c>author:@me</c>). Backs the "My PRs" view. Only called by
    /// the orchestrator when <see cref="SourceCapabilities.SupportsAuthoredInbox"/>
    /// is <c>true</c>; sources that don't support it may return an empty stream.
    /// </summary>
    IAsyncEnumerable<RemotePullRequest> ListAuthoredFastAsync(CancellationToken ct);

    /// <summary>
    /// Tier-3 enrichment: returns full detail (head/base SHAs, ordered commits,
    /// reviewer state) plus all observed threads for a single PR, bundled into
    /// a single bundle so the orchestrator can persist them atomically.
    /// </summary>
    Task<PrEnrichmentBundle> EnrichAsync(PrIdentity id, CancellationToken ct);

    /// <summary>
    /// Commits on a PR's head branch, ordered newest-first. Defined for
    /// future force-push detection; not yet consumed by the orchestrator.
    /// </summary>
    Task<IReadOnlyList<RemoteCommit>> GetCommitsAsync(PrIdentity id, CancellationToken ct);

    /// <summary>
    /// Compare a previous head SHA to current head; detects force-push
    /// (the prior SHA being unreachable from current head) and gives ahead/behind counts.
    /// </summary>
    Task<CompareResult> CompareAsync(PrIdentity id, string previousHeadSha, string currentHeadSha, CancellationToken ct);
}
