using PrInbox.Core.Findings;

namespace PrInbox.Web.Services;

/// <summary>
/// Derives a convergence verdict for a findings-bearing review run — the
/// findings-counts analogue of <see cref="CleanVerdict"/>. Tells the user
/// at scan-distance whether both reviewer models agree on every finding
/// (high confidence) or one model flagged things the other did not.
/// </summary>
/// <remarks>
/// <para>Decision matrix:</para>
/// <list type="bullet">
///   <item><c>Findings.Count == 0</c> -> <see cref="ConvergenceState.Hidden"/>.
///         The clean-pill already handles that case.</item>
///   <item><c>Models.Count &lt; 2</c> -> <see cref="ConvergenceState.Hidden"/>.
///         You cannot converge with one reviewer.</item>
///   <item>No <see cref="AsymmetryStats"/> block, or all three counters
///         are zero -> <see cref="ConvergenceState.Hidden"/>. No claim.</item>
///   <item>Only <c>BothFound</c> &gt; 0 (opus_only and gpt_only both zero)
///         -> <see cref="ConvergenceState.Converged"/>. Every finding the
///         document carries was independently flagged by both reviewers.</item>
///   <item>Any single-model counter &gt; 0 -> <see cref="ConvergenceState.Asymmetric"/>.
///         At least one finding came from one reviewer only — the hover
///         tooltip surfaces the breakdown.</item>
/// </list>
/// </remarks>
public static class ConvergenceVerdict
{
    /// <summary>
    /// Compute the badge state + display data for a findings-bearing
    /// review. Safe to call with a doc that has zero findings; you'll
    /// just get back <see cref="ConvergenceState.Hidden"/>.
    /// </summary>
    public static ConvergenceBadge Compute(FindingsDocument doc)
    {
        if (doc.Findings.Count == 0) return Hidden;
        if (doc.Models.Count < 2)    return Hidden;
        if (doc.Asymmetry is null)   return Hidden;

        var both     = doc.Asymmetry.BothFound;
        var opusOnly = doc.Asymmetry.OpusOnly ?? 0;
        var gptOnly  = doc.Asymmetry.GptOnly  ?? 0;
        var total    = both + opusOnly + gptOnly;
        if (total == 0) return Hidden;

        if (opusOnly == 0 && gptOnly == 0)
        {
            return new ConvergenceBadge(
                State: ConvergenceState.Converged,
                Glyph: "✓✓",
                CssClass: "converged",
                Tooltip: BuildConvergedTooltip(doc, both));
        }

        return new ConvergenceBadge(
            State: ConvergenceState.Asymmetric,
            Glyph: "⚠",
            CssClass: "asymmetric",
            Tooltip: BuildAsymmetricTooltip(doc, both, opusOnly, gptOnly));
    }

    private static readonly ConvergenceBadge Hidden = new(
        State: ConvergenceState.Hidden,
        Glyph: string.Empty,
        CssClass: string.Empty,
        Tooltip: string.Empty);

    private static string BuildConvergedTooltip(FindingsDocument doc, int both)
    {
        var modelsStr = doc.Models.Count > 0 ? string.Join(" + ", doc.Models) : null;
        var part = $"Both reviewers flagged every finding · {both} agreed";
        if (modelsStr is not null) part += $" · {modelsStr}";
        return part;
    }

    private static string BuildAsymmetricTooltip(FindingsDocument doc, int both, int opusOnly, int gptOnly)
    {
        // Two model names — fall back to "model A / B" labels if Models
        // doesn't have exactly two entries (which would be unusual but
        // not invalid).
        string opusLabel = "Opus only";
        string gptLabel  = "GPT only";
        if (doc.Models.Count == 2)
        {
            opusLabel = $"{doc.Models[0]} only";
            gptLabel  = $"{doc.Models[1]} only";
        }
        return $"Asymmetric · both: {both} · {opusLabel}: {opusOnly} · {gptLabel}: {gptOnly} — review per-finding attribution";
    }
}

/// <summary>State of the convergence badge for a findings-bearing run.</summary>
public enum ConvergenceState
{
    /// <summary>Do not render a badge.</summary>
    Hidden,
    /// <summary>All findings flagged by every reviewer. High confidence.</summary>
    Converged,
    /// <summary>At least one finding came from one reviewer only.</summary>
    Asymmetric,
}

/// <summary>Render-ready badge data for an inbox row.</summary>
public sealed record ConvergenceBadge(
    ConvergenceState State,
    string Glyph,
    string CssClass,
    string Tooltip);
