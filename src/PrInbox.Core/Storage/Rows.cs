using PrInbox.Core.Models;

namespace PrInbox.Core.Storage;

/// <summary>
/// Row from <c>pull_requests</c>. The current-row truth for a PR; never deleted.
/// </summary>
public sealed record PullRequestRow(
    PrIdentity Identity,
    SourceKind SourceKind,
    string SourceId,
    string DisplayRepo,
    int Number,
    string? Title,
    string? AuthorLogin,
    string Url,
    PullRequestStatus Status,
    TrackingReason TrackingReason,
    string IdentityUsed,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSyncedAt,
    EnrichState EnrichState,
    string? LastBriefedHeadSha,
    string? LastReviewRunHeadSha,
    string? LastPostedReviewHeadSha);

/// <summary>
/// Row from <c>pr_snapshots</c>. Append-only.
/// </summary>
public sealed record PrSnapshotRow(
    long Id,
    PrIdentity Identity,
    DateTimeOffset SyncedAt,
    string HeadSha,
    string BaseSha,
    string? MergeBaseSha,
    IReadOnlyList<string> OrderedCommitShas,
    ReviewerState? ReviewerState,
    PullRequestStatus PrState,
    string? RawMetadataJson);

/// <summary>
/// Row from <c>observed_threads</c>. <c>first_seen_at</c> is preserved across
/// updates; <c>last_seen_at</c> moves forward; <c>resolved_at</c> is set
/// when the platform reports the thread resolved.
/// </summary>
public sealed record ObservedThreadRow(
    long Id,
    PrIdentity Identity,
    string PlatformThreadId,
    ThreadKind Kind,
    string? AuthorLogin,
    bool IsBot,
    BotKind? BotKind,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? ResolvedAt,
    string? RawJson);

/// <summary>
/// Row from <c>review_runs</c>. Immutable once created.
/// </summary>
public sealed record ReviewRunRow(
    long Id,
    PrIdentity Identity,
    DateTimeOffset CreatedAt,
    string BriefPath,
    string RunDirectory,
    string HeadSha,
    string BaseSha,
    ReviewRunStatus Status,
    string? CopilotSessionId,
    string? Notes);

/// <summary>
/// Row from <c>posted_reviews</c>. Written only by the publisher project.
/// Append-only; one row per successful "publish" call. Identifies which
/// findings landed remotely so subsequent posts don't duplicate them.
/// </summary>
public sealed record PostedReviewRow(
    long Id,
    PrIdentity Identity,
    long? ReviewRunId,
    string PlatformReviewId,
    string? ReviewUrl,
    DateTimeOffset PostedAt,
    string HeadShaAtPost,
    string IdentityUsed,
    int InlineCount,
    bool BodyPresent,
    IReadOnlyList<string> FindingIds,
    IReadOnlyList<string> FindingFingerprints,
    bool DryRun);

/// <summary>
/// Row from <c>sync_runs</c>. Created with <see cref="SyncRunStatus.Running"/>
/// and finalized with status + completed_at + prs_seen + optional error.
/// </summary>
public sealed record SyncRunRow(
    long Id,
    string SourceId,
    string IdentityUsed,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    SyncRunStatus Status,
    string? Error,
    int PrsSeen);
