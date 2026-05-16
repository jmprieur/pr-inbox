namespace PrInbox.Core.Models;

/// <summary>
/// Minimal projection of a pull request returned from a source's review-inbox query.
/// Carries just enough to populate the registry's current-row truth.
/// </summary>
public sealed record RemotePullRequest(
    PrIdentity Identity,
    SourceKind SourceKind,
    string SourceId,
    string DisplayRepo,
    int Number,
    string? Title,
    string? AuthorLogin,
    string Url,
    PullRequestStatus Status,
    DateTimeOffset LastUpdated);

/// <summary>
/// Full detail for a single PR, fetched once per sync. Carries enough state
/// to populate <c>pr_snapshots</c> and feed the review brief generator.
/// </summary>
public sealed record RemotePullRequestDetail(
    PrIdentity Identity,
    string HeadSha,
    string BaseSha,
    string? MergeBaseSha,
    IReadOnlyList<string> OrderedCommitShas,
    ReviewerState? ReviewerState,
    PullRequestStatus Status,
    string RawMetadataJson,
    string? Body = null,
    IReadOnlyList<RemoteFileChange>? Files = null,
    string? MergeableState = null,
    string? CiStatus = null);

/// <summary>
/// One file changed in a PR. Populated when the source adapter supports a
/// cheap files endpoint (GitHub does via <c>pulls/{n}/files</c>); may be
/// <c>null</c> on adapters that don't (ADO defers it).
/// </summary>
public sealed record RemoteFileChange(
    string Path,
    int Additions,
    int Deletions,
    string? Status);

/// <summary>
/// Bundled result of a single tier-3 enrichment call for one PR: per-PR
/// detail plus threads. Sources should fetch both in one logical call so the
/// orchestrator can perform an enrichment as an atomic operation per PR.
/// </summary>
public sealed record PrEnrichmentBundle(
    RemotePullRequestDetail Detail,
    IReadOnlyList<RemoteThread> Threads);

/// <summary>
/// A single conversation thread on a PR (inline comment, issue comment,
/// review body, or ADO thread).
/// </summary>
public sealed record RemoteThread(
    string PlatformThreadId,
    ThreadKind Kind,
    string? AuthorLogin,
    bool IsBot,
    BotKind? BotKind,
    bool IsResolved,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUpdatedAt,
    string RawJson,
    string? BodyExcerpt = null,
    string? AnchorPath = null,
    int? AnchorLine = null);

/// <summary>
/// A commit on a PR's head branch.
/// </summary>
public sealed record RemoteCommit(
    string Sha,
    string? AuthorLogin,
    DateTimeOffset CommittedAt,
    string Subject);

/// <summary>
/// Reachability + diff outcome for comparing two SHAs on the same head branch.
/// Used by force-push detection.
/// </summary>
/// <param name="BaseUnreachableFromHead">
/// <c>true</c> when the supplied <c>baseSha</c> is no longer reachable
/// from current head (i.e. the author force-pushed and rewrote history).
/// </param>
/// <param name="CommitsAhead">
/// Number of commits in current head not in base (zero if base is current head).
/// </param>
/// <param name="CommitsBehind">
/// Number of commits in base not in head (zero if no force-push).
/// </param>
public sealed record CompareResult(
    bool BaseUnreachableFromHead,
    int CommitsAhead,
    int CommitsBehind);
