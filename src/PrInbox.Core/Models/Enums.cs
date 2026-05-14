namespace PrInbox.Core.Models;

/// <summary>
/// Overall lifecycle status of a pull request, normalized across platforms.
/// </summary>
public enum PullRequestStatus
{
    /// <summary>Open and accepting changes.</summary>
    Open,
    /// <summary>Closed without merging.</summary>
    Closed,
    /// <summary>Merged.</summary>
    Merged,
    /// <summary>Visible-but-not-readable (e.g. 404 / 403 after access change).</summary>
    Inaccessible,
}

/// <summary>
/// Why <c>pr-inbox</c> keeps tracking a PR. Lifecycle is orthogonal to
/// <see cref="PullRequestStatus"/>: a <c>previously_assigned</c> PR can be
/// <c>open</c> and still want attention because of follow-up activity.
/// </summary>
public enum TrackingReason
{
    /// <summary>Jean-Marc is currently a requested reviewer.</summary>
    Assigned,
    /// <summary>Was a reviewer; assignment dropped but follow-up activity remains.</summary>
    PreviouslyAssigned,
    /// <summary>User added via <c>pr-inbox add</c> (v0.2+).</summary>
    ManuallyAdded,
    /// <summary>User-archived; ignored by default in <c>list</c>.</summary>
    Archived,
}

/// <summary>
/// The reviewer's state on a PR. Normalized across platforms; may be null
/// if the platform doesn't report one (e.g. ADO before vote).
/// </summary>
public enum ReviewerState
{
    Requested,
    Approved,
    ApprovedWithSuggestions,
    ChangesRequested,
    Commented,
    Dismissed,
    Waiting,
}

/// <summary>
/// The kind of platform conversation surface a thread lives on.
/// </summary>
public enum ThreadKind
{
    ReviewComment,   // GitHub inline review comment
    IssueComment,    // GitHub PR conversation / issue comment
    ReviewBody,      // GitHub review top-level body
    AdoThread,       // ADO PR thread (inline or top-level)
}

/// <summary>
/// Categorization of a known bot author, used to surface "Copilot commented"
/// vs other automation in <c>list</c> and in the review brief.
/// </summary>
public enum BotKind
{
    CopilotReview,
    CopilotCodingAgent,
    GitHubActions,
    Dependabot,
    Other,
}

/// <summary>
/// Status of a <c>review_runs</c> row.
/// </summary>
public enum ReviewRunStatus
{
    /// <summary>Brief generated; user has not yet opened a Copilot session.</summary>
    Generated,
    /// <summary>A Copilot session was opened against this run.</summary>
    SessionStarted,
    /// <summary>User explicitly abandoned this run.</summary>
    Abandoned,
    /// <summary>A later run for the same PR replaced this one as the active brief.</summary>
    Superseded,
}

/// <summary>
/// Outcome of a single sync attempt for one (source, identity) pair.
/// </summary>
public enum SyncRunStatus
{
    Running,
    Ok,
    Partial,
    Failed,
    RateLimited,
}
