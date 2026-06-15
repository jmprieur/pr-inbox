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
    /// <summary>
    /// Reviewer lifecycle does not apply: the row is tracked solely because
    /// the user <em>authored</em> it (<see cref="MyRole.Author"/>). This is a
    /// lifecycle sentinel, not a role — role lives in <see cref="MyRole"/>.
    /// The reviewer disappear-sweep skips these rows because they are never
    /// <see cref="Assigned"/>.
    /// </summary>
    NotReviewer,
}

/// <summary>
/// The authenticated user's role on a PR, orthogonal to
/// <see cref="TrackingReason"/> (reviewer lifecycle) and
/// <see cref="PullRequestStatus"/>. Drives which view a PR appears in: the
/// reviewer inbox shows <see cref="Reviewer"/>/<see cref="Both"/>; the
/// "My PRs" view shows <see cref="Author"/>/<see cref="Both"/>.
/// </summary>
public enum MyRole
{
    /// <summary>The user is (or was) a reviewer on this PR. Default for every
    /// row that predates the authored-inbox feature.</summary>
    Reviewer,
    /// <summary>The user authored this PR.</summary>
    Author,
    /// <summary>The user both authored and reviews this PR (rare; e.g. ADO
    /// self-add). Appears in both views.</summary>
    Both,
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
