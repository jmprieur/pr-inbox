namespace PrInbox.Core.Models;

/// <summary>
/// Capability flags for a source adapter. Allows callers to vary behavior
/// without leaking platform-specific conditionals everywhere.
/// </summary>
/// <param name="SupportsGlobalReviewerInbox">
/// <c>true</c> if the platform has a single API call equivalent to
/// "give me all PRs I'm a reviewer on." <c>false</c> for Azure DevOps,
/// which requires per-project enumeration.
/// </param>
/// <param name="SupportsThreadResolution">
/// <c>true</c> if the platform exposes a resolved/unresolved bit on threads.
/// </param>
/// <param name="SupportsBotAuthorClassification">
/// <c>true</c> if the platform identifies bot authors out-of-band
/// (e.g. GitHub's <c>User.Type == "Bot"</c>).
/// </param>
/// <param name="SupportsReviewRequestTimestamps">
/// <c>true</c> if the platform records when a reviewer was requested.
/// </param>
/// <param name="SupportsStableRepoIds">
/// <c>true</c> if the platform exposes immutable repository IDs that
/// survive rename. Required for the stable identity scheme.
/// </param>
/// <param name="SupportsForcePushDetection">
/// <c>true</c> if the platform exposes enough commit history to detect
/// when the prior reviewed HEAD is no longer reachable from current HEAD.
/// </param>
/// <param name="SupportsAuthoredInbox">
/// <c>true</c> if the platform can list PRs the authenticated user
/// <em>authored</em> in a single query (GitHub's <c>author:@me</c>).
/// <c>false</c> for Azure DevOps until per-project <c>creatorId</c>
/// enumeration is implemented. Gates the orchestrator's authored pass.
/// </param>
public sealed record SourceCapabilities(
    bool SupportsGlobalReviewerInbox,
    bool SupportsThreadResolution,
    bool SupportsBotAuthorClassification,
    bool SupportsReviewRequestTimestamps,
    bool SupportsStableRepoIds,
    bool SupportsForcePushDetection,
    bool SupportsAuthoredInbox = false);
