using System.Text.RegularExpressions;

namespace PrInbox.Web.Services;

/// <summary>
/// Heuristic that decides whether a comment body looks like a "this thread
/// is done" reply. Tuned conservatively to favour precision over recall:
/// the user clicks Resolve themselves, so false-negatives are cheap (one
/// missed badge) while false-positives are annoying (a "done" badge on a
/// thread that's still under discussion).
/// </summary>
/// <remarks>
/// <para>The body comes from <c>observed_threads.last_comment_body</c>
/// which is already whitespace-collapsed to a single line by the GitHub
/// adapter (<see href="GitHubReadSource.TruncateExcerpt"/>). We therefore
/// only match at the start of the string.</para>
///
/// <para>Patterns accepted (case-insensitive, optional leading
/// markdown-list / quote prefix like <c>&gt; </c> or <c>* </c>):</para>
/// <list type="bullet">
///   <item><c>done</c>, <c>fixed</c>, <c>resolved</c>, <c>addressed</c>,
///         <c>ack</c>, <c>acknowledged</c> — as the leading verb,
///         followed by a sentence boundary (<c>.</c> / <c>,</c> /
///         <c>!</c> / end of body) or <c>" in &lt;token&gt;"</c>
///         (commit SHA, PR ref, etc).</item>
///   <item>Bare <c>+1</c> as a whole-body acknowledgement.</item>
/// </list>
///
/// <para>Examples that match:</para>
/// <list type="bullet">
///   <item><c>Done.</c></item>
///   <item><c>Fixed in abc1234.</c></item>
///   <item><c>Done in e0193224. `inject(0)` is back as the traversal anchor...</c></item>
///   <item><c>+1</c></item>
/// </list>
///
/// <para>Examples that do NOT match (deliberately):</para>
/// <list type="bullet">
///   <item><c>Done thoroughly checked the code...</c> — no boundary after the verb.</item>
///   <item><c>not done</c> / <c>won't fix</c> / <c>wip</c> — negations / wrong intent.</item>
///   <item><c>This is a great point, will fix later.</c> — verb not at start.</item>
/// </list>
/// </remarks>
public static class DoneReplyHeuristic
{
    private const string LeadingNoise = @"[\s>*\-]*";

    private const string DoneVerb =
        @"(done|fixed|resolved|addressed|ack(nowledged)?)";

    private const string DoneBoundary =
        @"\s*(?:[.,!]|$|in\s+\S+)";

    private static readonly Regex VerbPattern = new(
        $"^{LeadingNoise}{DoneVerb}\\b{DoneBoundary}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PlusOnePattern = new(
        $@"^{LeadingNoise}\+1\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// <c>true</c> when <paramref name="body"/> looks like a "this thread
    /// is done" reply per the regex matrix in the type comment.
    /// <paramref name="body"/> may be null/whitespace (returns false).
    /// </summary>
    public static bool IsDoneReply(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        if (PlusOnePattern.IsMatch(body)) return true;
        return VerbPattern.IsMatch(body);
    }
}
