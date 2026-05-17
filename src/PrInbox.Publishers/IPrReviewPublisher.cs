using PrInbox.Core.Findings;

namespace PrInbox.Publishers;

/// <summary>
/// The high-level state to set on the review when posting.
/// Mirrors GitHub's review <c>event</c> field.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><see cref="Comment"/> — file plain review comments; no vote.</item>
///   <item><see cref="Approve"/> — explicit thumbs-up review.</item>
///   <item><see cref="RequestChanges"/> — explicit thumbs-down review.</item>
/// </list>
/// Azure DevOps has no direct equivalent; the ADO publisher rejects
/// anything other than <see cref="Comment"/>.
/// </remarks>
public enum ReviewEvent
{
    Comment = 0,
    Approve = 1,
    RequestChanges = 2,
}

/// <summary>
/// One finding selected by the user for posting. A simplified, transport-
/// ready projection of a <see cref="Finding"/> from findings.yaml.
/// </summary>
/// <remarks>
/// <see cref="DiffAnchorable"/> drives how the publisher renders the
/// finding: <c>true</c> → inline comment on a diff hunk; <c>false</c> →
/// appended to the review body as a numbered, file/line-tagged entry so
/// the reviewer can still find it without losing the warning.
/// </remarks>
public sealed record FindingToPost(
    string Id,
    FindingSeverity Severity,
    FindingConfidence Confidence,
    IReadOnlyList<string> FoundBy,
    string File,
    int Line,
    int? LineEnd,
    bool DiffAnchorable,
    string Title,
    string Body,
    string? SuggestedInline);

/// <summary>
/// Request to publish a selection of findings against one PR. Always
/// constructed by the web companion; never accessible from the CLI.
/// </summary>
/// <param name="PrUrl">Canonical PR URL (already normalised).</param>
/// <param name="RunId">
/// The <c>review_runs.id</c> these findings came from. Null only for
/// ad-hoc posts that didn't go through a review run (not expected in v0.1).
/// </param>
/// <param name="HeadShaAtAuthoring">
/// The HEAD sha that was current when the brief was built. Used to detect
/// drift; never used to gate a post.
/// </param>
/// <param name="ReviewBodyHeader">
/// Markdown for the review body (free-form provenance/header text). Inline
/// findings post separately as comments on that review; non-anchorable
/// findings are appended to this body.
/// </param>
/// <param name="Findings">Selected findings, in display order.</param>
/// <param name="DryRun">
/// <c>true</c> ⇒ no network traffic at all. The publisher returns a
/// well-formed <see cref="PublishResult"/> describing what it WOULD have
/// posted; it does not write to <c>posted_reviews</c>. Default behaviour
/// at the API boundary in the web companion is <c>true</c>.
/// </param>
/// <param name="ValidateRemoteState">
/// When <c>DryRun</c> is <c>false</c>, fetch current HEAD and compare to
/// <see cref="HeadShaAtAuthoring"/>; surface a warning if changed. Ignored
/// (forced false) when <c>DryRun</c> is <c>true</c>.
/// </param>
/// <param name="Event">
/// GitHub-style review verb: comment / approve / request changes. Defaults
/// to <see cref="ReviewEvent.Comment"/> to preserve existing behaviour.
/// </param>
public sealed record PublishRequest(
    string PrUrl,
    long? RunId,
    string HeadShaAtAuthoring,
    string ReviewBodyHeader,
    IReadOnlyList<FindingToPost> Findings,
    bool DryRun,
    bool ValidateRemoteState,
    ReviewEvent Event = ReviewEvent.Comment);

/// <summary>
/// Outcome of a publish call. Never throws on policy violations (HEAD
/// drift, partial inline failures); reports them through warnings.
/// </summary>
public sealed record PublishResult(
    bool Posted,                 // true ⇔ a real POST returned 2xx
    string? PlatformReviewId,    // GH review id / ADO thread id
    string? ReviewUrl,           // html_url if available
    int InlineCount,             // inline comments that succeeded
    int BodyOnlyCount,           // non-anchorable findings written into body
    int SkippedAsAlreadyPosted,  // duplicates suppressed by idempotency
    string? HeadShaAtPost,       // resolved HEAD when posting (null in dry-run)
    bool HeadChanged,
    string IdentityUsed,         // identity / persona of the token used
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public static PublishResult DryRunPlan(
        int inlineCount,
        int bodyOnlyCount,
        int skipped,
        string identityUsed,
        string warning)
        => new(
            Posted: false,
            PlatformReviewId: null,
            ReviewUrl: null,
            InlineCount: inlineCount,
            BodyOnlyCount: bodyOnlyCount,
            SkippedAsAlreadyPosted: skipped,
            HeadShaAtPost: null,
            HeadChanged: false,
            IdentityUsed: identityUsed,
            Warnings: new[] { warning },
            Errors: Array.Empty<string>());

    public static PublishResult Failure(string identityUsed, params string[] errors)
        => new(
            Posted: false,
            PlatformReviewId: null,
            ReviewUrl: null,
            InlineCount: 0,
            BodyOnlyCount: 0,
            SkippedAsAlreadyPosted: 0,
            HeadShaAtPost: null,
            HeadChanged: false,
            IdentityUsed: identityUsed,
            Warnings: Array.Empty<string>(),
            Errors: errors);
}

/// <summary>
/// Request to resolve one or more PR review threads on the source platform.
/// </summary>
/// <param name="PrUrl">Canonical PR URL (already normalised).</param>
/// <param name="ThreadNodeIds">
/// Platform-native thread handles to resolve. For GitHub these are the
/// GraphQL <c>ReviewThread.id</c> values stored on
/// <c>observed_threads.platform_thread_node_id</c>. The publisher does NOT
/// validate that these belong to <paramref name="PrUrl"/>; the orchestrator
/// is responsible for ensuring the caller only supplies known-belonging ids
/// (server-side authoritative validation against the local DB).
/// </param>
/// <param name="DryRun">
/// <c>true</c> ⇒ no network traffic. The publisher returns a result
/// describing what it WOULD have done.
/// </param>
public sealed record ThreadResolveRequest(
    string PrUrl,
    IReadOnlyList<string> ThreadNodeIds,
    bool DryRun);

/// <summary>
/// Outcome of a <see cref="IPrReviewPublisher.ResolveThreadsAsync"/> call.
/// Per-thread outcomes are reported via the three disjoint lists so the
/// orchestrator knows exactly which local <c>observed_threads</c> rows to
/// stamp resolved.
/// </summary>
/// <param name="Performed">
/// <c>true</c> when at least one live mutation was issued (i.e. not a
/// dry-run). Independent of whether any individual mutation succeeded.
/// </param>
/// <param name="ResolvedNodeIds">
/// Threads we resolved as a direct result of this call.
/// </param>
/// <param name="AlreadyResolvedNodeIds">
/// Threads the server reported as already resolved (race with another
/// reviewer, or duplicated click). Treated as success — orchestrator
/// should still mark these resolved locally.
/// </param>
/// <param name="FailedNodeIds">
/// Threads the mutation failed on (auth, permissions, GitHub-side errors).
/// Orchestrator must NOT mark these resolved locally.
/// </param>
public sealed record ThreadResolveResult(
    bool Performed,
    IReadOnlyList<string> ResolvedNodeIds,
    IReadOnlyList<string> AlreadyResolvedNodeIds,
    IReadOnlyList<string> FailedNodeIds,
    string IdentityUsed,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public static ThreadResolveResult Failure(string identityUsed, params string[] errors)
        => new(
            Performed: false,
            ResolvedNodeIds: Array.Empty<string>(),
            AlreadyResolvedNodeIds: Array.Empty<string>(),
            FailedNodeIds: Array.Empty<string>(),
            IdentityUsed: identityUsed,
            Warnings: Array.Empty<string>(),
            Errors: errors);

    public static ThreadResolveResult DryRunPlan(
        IReadOnlyList<string> wouldResolve,
        string identityUsed,
        string warning)
        => new(
            Performed: false,
            ResolvedNodeIds: wouldResolve,
            AlreadyResolvedNodeIds: Array.Empty<string>(),
            FailedNodeIds: Array.Empty<string>(),
            IdentityUsed: identityUsed,
            Warnings: new[] { warning },
            Errors: Array.Empty<string>());
}

/// <summary>
/// Posts a selection of findings to the source platform (GitHub.com, GHE,
/// ADO). Implementations: <c>GitHubReviewPublisher</c>,
/// <c>AdoReviewPublisher</c>. Selection of the right implementation is
/// done by <see cref="IPublisherSelector"/>.
/// </summary>
public interface IPrReviewPublisher
{
    /// <summary>
    /// Stable identifier for this publisher (e.g. <c>github</c>,
    /// <c>github-enterprise</c>, <c>azure-devops</c>). Used for logging
    /// and for the result.IdentityUsed prefix.
    /// </summary>
    string Kind { get; }

    /// <summary>
    /// Execute the publish. Implementations MUST treat
    /// <see cref="PublishRequest.DryRun"/> as a hard "no network" gate.
    /// </summary>
    Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken ct);

    /// <summary>
    /// Mark one or more PR review threads as resolved on the source
    /// platform. Implementations MUST treat <see cref="ThreadResolveRequest.DryRun"/>
    /// as a hard "no network" gate. Idempotent: re-resolving a thread that
    /// is already resolved upstream must report it as
    /// <see cref="ThreadResolveResult.AlreadyResolvedNodeIds"/>, not as a
    /// failure.
    /// </summary>
    Task<ThreadResolveResult> ResolveThreadsAsync(ThreadResolveRequest request, CancellationToken ct);
}
