using PrInbox.Web.Services;

namespace PrInbox.Tests.Web;

/// <summary>
/// Tests for <see cref="DoneReplyHeuristic.IsDoneReply(string?)"/>. The
/// heuristic decides whether a thread's latest comment body looks like a
/// "this is fixed" confirmation. The matrix is tuned conservatively —
/// false-positives (badge on a thread still under discussion) are more
/// annoying than false-negatives (a missed "done" badge), so the pattern
/// requires the verb at the start of the line followed by a sentence
/// boundary or "in &lt;token&gt;".
/// </summary>
public class DoneReplyHeuristicTests
{
    [Theory]
    // Bare verbs, with and without trailing punctuation.
    [InlineData("done")]
    [InlineData("Done")]
    [InlineData("DONE")]
    [InlineData("done.")]
    [InlineData("Done!")]
    [InlineData("fixed")]
    [InlineData("Fixed.")]
    [InlineData("resolved")]
    [InlineData("Resolved.")]
    [InlineData("addressed")]
    [InlineData("Addressed.")]
    [InlineData("ack")]
    [InlineData("Ack.")]
    [InlineData("acknowledged")]
    [InlineData("Acknowledged.")]
    // "in <token>" variants — the most common Copilot-coding-agent voice.
    [InlineData("Done in e0193224.")]
    [InlineData("Fixed in abc1234")]
    [InlineData("done in #42")]
    [InlineData("Resolved in PR #123.")]
    // Followed by an explanation — body collapses to a single line via
    // GitHubReadSource.TruncateExcerpt, so the pattern only checks the
    // leading verb-boundary.
    [InlineData("Done in e0193224. `inject(0)` is back as the traversal anchor.")]
    [InlineData("Fixed. The retry now uses Retry-After.")]
    [InlineData("Addressed, thanks for the catch.")]
    // Markdown-list / quote prefixes.
    [InlineData("> done")]
    [InlineData("* Fixed.")]
    [InlineData("- Resolved")]
    // Bare +1 acknowledgement.
    [InlineData("+1")]
    [InlineData(" +1 ")]
    public void IsDoneReply_TruePositives(string body)
    {
        Assert.True(DoneReplyHeuristic.IsDoneReply(body),
            $"Expected '{body}' to be classified as a 'done' reply.");
    }

    [Theory]
    // Empty / whitespace / null.
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // Negations and wrong intent.
    [InlineData("not done")]
    [InlineData("won't fix")]
    [InlineData("wontfix")]
    [InlineData("wip")]
    [InlineData("in progress")]
    [InlineData("will not address")]
    // Verb is present but not at start of line.
    [InlineData("This is a great point, will fix later.")]
    [InlineData("Once I'm done with the refactor I'll come back to this.")]
    [InlineData("Considering this fixed once tests pass.")]
    // Verb at start but no sentence boundary — could be an unrelated word.
    [InlineData("Done thoroughly checked the code, found another issue.")]
    [InlineData("Donethorough")]
    [InlineData("Fixedinside the wrong helper.")]
    // +1 with following text — not a bare acknowledgement.
    [InlineData("+1 but consider this edge case")]
    public void IsDoneReply_TrueNegatives(string? body)
    {
        Assert.False(DoneReplyHeuristic.IsDoneReply(body),
            $"Expected '{body ?? "<null>"}' NOT to be classified as a 'done' reply.");
    }
}
