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
    /// Stable identifier for this source instance (e.g. <c>gh.com</c>,
    /// <c>ghe.contoso.com</c>, <c>ado:mseng</c>).
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
    /// All PRs where the authenticated user is currently a requested reviewer.
    /// For ADO (which has no global reviewer inbox), implementations enumerate
    /// the configured (org, project) pairs.
    /// </summary>
    Task<IReadOnlyList<RemotePullRequest>> GetReviewInboxAsync(CancellationToken ct);

    /// <summary>
    /// Full detail for a single PR, including head/base SHAs, ordered commit
    /// SHAs, and reviewer state.
    /// </summary>
    Task<RemotePullRequestDetail> GetPullRequestDetailAsync(PrIdentity id, CancellationToken ct);

    /// <summary>
    /// All threads (inline comments, issue comments, review bodies, ADO threads)
    /// on a PR, with bot classification set.
    /// </summary>
    Task<IReadOnlyList<RemoteThread>> GetThreadsAsync(PrIdentity id, CancellationToken ct);

    /// <summary>
    /// Commits on a PR's head branch, ordered newest-first.
    /// </summary>
    Task<IReadOnlyList<RemoteCommit>> GetCommitsAsync(PrIdentity id, CancellationToken ct);

    /// <summary>
    /// Compare a previous head SHA to current head; detects force-push
    /// (the prior SHA being unreachable from current head) and gives ahead/behind counts.
    /// </summary>
    Task<CompareResult> CompareAsync(PrIdentity id, string previousHeadSha, string currentHeadSha, CancellationToken ct);
}
