using PrInbox.Core.Findings;

namespace PrInbox.Web.Services;

/// <summary>
/// Derives a human-readable verdict for a clean (zero-findings) review run.
/// All inputs come straight from <see cref="FindingsDocument"/> — no DB
/// persistence — so a server restart that rehydrates findings.yaml still
/// produces the same verdict.
/// </summary>
/// <remarks>
/// Convergence semantics: two or more reviewer models ran and an explicit
/// <c>asymmetry</c> block reports zero on every counter. That's the
/// strongest signal the dual-model-review protocol emits — two
/// structurally-different reviewers independently arrived at "no findings"
/// on the same diff. The inbox badge shouldn't waste pixels saying so,
/// but the hover tooltip and the review-page callout both surface it.
/// </remarks>
public static class CleanVerdict
{
    /// <summary>
    /// True when the doc represents a multi-reviewer converged-clean run.
    /// Pre-condition: caller has already confirmed <c>Findings.Count == 0</c>;
    /// this method does not re-check that (so a future "non-empty
    /// converged" case stays open as a separate notion).
    /// </summary>
    public static bool IsConverged(FindingsDocument doc)
    {
        return doc.Models.Count >= 2
            && doc.Asymmetry is { BothFound: 0 } asym
            && (asym.OpusOnly ?? 0) == 0
            && (asym.GptOnly ?? 0) == 0;
    }

    /// <summary>
    /// Build the verdict string shown in the inbox-row tooltip and inline
    /// in the Review-page callout. Examples:
    /// <list type="bullet">
    ///   <item>"Reviewed clean 2h ago · 2 reviewers agree · claude-opus-4.8 + gpt-5.5"</item>
    ///   <item>"Reviewed clean 2h ago · 2 reviewers · claude-opus-4.8 + gpt-5.5" (no asymmetry block in the doc)</item>
    ///   <item>"Reviewed clean 5m ago · claude-opus-4.8" (single reviewer)</item>
    ///   <item>"Reviewed clean just now" (no models declared)</item>
    /// </list>
    /// </summary>
    public static string BuildTooltip(FindingsDocument doc, DateTimeOffset nowUtc)
    {
        var when = FormatRelative(nowUtc - doc.GeneratedAtUtc);
        var modelsStr = doc.Models.Count > 0 ? string.Join(" + ", doc.Models) : null;

        if (IsConverged(doc))
        {
            var part = $"Reviewed clean {when} · {doc.Models.Count} reviewers agree";
            if (modelsStr is not null) part += $" · {modelsStr}";
            return part;
        }

        if (doc.Models.Count >= 2)
        {
            var part = $"Reviewed clean {when} · {doc.Models.Count} reviewers";
            if (modelsStr is not null) part += $" · {modelsStr}";
            return part;
        }

        var single = $"Reviewed clean {when}";
        if (modelsStr is not null) single += $" · {modelsStr}";
        return single;
    }

    private static string FormatRelative(TimeSpan delta)
    {
        if (delta.TotalSeconds < 60) return "just now";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours   < 24) return $"{(int)delta.TotalHours}h ago";
        return $"{(int)delta.TotalDays}d ago";
    }
}
