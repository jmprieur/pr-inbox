using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PrInbox.Publishers;

/// <summary>
/// Helpers shared by all <see cref="IPrReviewPublisher"/> implementations.
/// Stateless, pure functions only.
/// </summary>
/// <remarks>
/// Also consumed by the Web UI's Review page to render the exact text
/// that will land on the platform (preview-as-you-pick), so the methods
/// here are the single source of truth for what gets posted.
/// </remarks>
public static class PublishHelpers
{
    /// <summary>
    /// Build the per-finding fingerprint stored in <c>posted_reviews</c>.
    /// </summary>
    /// <remarks>
    /// Format: <c>"&lt;file&gt;|&lt;line&gt;|&lt;sha1(title)&gt;"</c>. The
    /// idea: even if a future re-run regenerates findings with new ids,
    /// the same issue at the same place with the same title hashes to the
    /// same key and the publisher will not re-post it.
    /// </remarks>
    public static string FingerprintOf(FindingToPost finding)
    {
        // Take a stable lowercase normalisation of the title to dodge
        // trivial whitespace/casing drift between runs.
        var normTitle = Regex.Replace(finding.Title.Trim().ToLowerInvariant(), @"\s+", " ");
        using var sha = SHA1.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normTitle));
        var hex = Convert.ToHexString(hash);
        return $"{finding.File}|{finding.Line}|{hex.Substring(0, 12)}";
    }

    /// <summary>
    /// Combine the user-supplied header with a formatted block listing any
    /// non-anchorable findings (so they're not lost from the review).
    /// </summary>
    public static string ComposeReviewBody(
        string header,
        IReadOnlyList<FindingToPost> nonAnchorable,
        string headSha)
    {
        var sb = new StringBuilder();
        sb.AppendLine(header.TrimEnd());
        sb.AppendLine();
        sb.AppendLine($"_Authored against head `{ShortSha(headSha)}`._");

        if (nonAnchorable.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Non-anchorable findings");
            sb.AppendLine();
            for (var i = 0; i < nonAnchorable.Count; i++)
            {
                var f = nonAnchorable[i];
                sb.AppendLine($"{i + 1}. **[{f.Severity.ToString().ToLowerInvariant()}]** " +
                              $"`{f.File}:{f.Line}` — {f.Title}");
                sb.AppendLine();
                sb.AppendLine(IndentLines(f.Body.TrimEnd(), 4));
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Compose the inline-comment body for a single anchorable finding
    /// (header line + body + suggested-inline code block if any).
    /// </summary>
    /// <remarks>
    /// Deliberately omits any "found by: model A, model B" attribution —
    /// per <c>posting-style.md</c>, comments stand on their own and
    /// don't disclose how the review was produced.
    /// </remarks>
    public static string ComposeInlineCommentBody(FindingToPost finding)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**[{finding.Severity.ToString().ToLowerInvariant()}]** {finding.Title}");
        sb.AppendLine();
        sb.AppendLine(finding.Body.TrimEnd());
        if (!string.IsNullOrWhiteSpace(finding.SuggestedInline))
        {
            sb.AppendLine();
            sb.AppendLine(finding.SuggestedInline.TrimEnd());
        }
        return sb.ToString();
    }

    public static string ShortSha(string sha)
        => string.IsNullOrEmpty(sha) ? "(unknown)" : sha.Substring(0, Math.Min(8, sha.Length));

    private static string IndentLines(string text, int spaces)
    {
        var pad = new string(' ', spaces);
        return string.Join('\n', text.Split('\n').Select(line => pad + line));
    }
}
