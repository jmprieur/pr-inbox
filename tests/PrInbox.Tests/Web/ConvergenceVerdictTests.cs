using PrInbox.Core.Findings;
using PrInbox.Web.Services;

namespace PrInbox.Tests.Web;

/// <summary>
/// Tests for <see cref="ConvergenceVerdict"/>. The badge drives the
/// findings-bearing inbox row's at-a-glance "confidence" cue: ✓✓ when
/// both reviewers found every finding, ⚠ when one model flagged things
/// the other did not, hidden when the doc lacks the data to make a
/// claim.
/// </summary>
public class ConvergenceVerdictTests
{
    private static FindingsDocument MakeDoc(
        int findingsCount = 1,
        IReadOnlyList<string>? models = null,
        AsymmetryStats? asymmetry = null)
    {
        var findings = new List<Finding>();
        for (var i = 0; i < findingsCount; i++)
        {
            findings.Add(new Finding { Id = $"f{i:00}", Title = $"Finding {i}" });
        }
        return new FindingsDocument
        {
            Models = models ?? Array.Empty<string>(),
            Asymmetry = asymmetry,
            Findings = findings,
        };
    }

    [Fact]
    public void Compute_ZeroFindings_Hidden()
    {
        // Clean runs are the clean-pill's job. This helper never claims
        // anything about them so the inbox row keeps a single green badge.
        var doc = MakeDoc(
            findingsCount: 0,
            models: new[] { "claude-opus-4.7", "gpt-5.5" },
            asymmetry: new AsymmetryStats { BothFound = 3, OpusOnly = 0, GptOnly = 0 });

        var badge = ConvergenceVerdict.Compute(doc);

        Assert.Equal(ConvergenceState.Hidden, badge.State);
    }

    [Fact]
    public void Compute_SingleModel_Hidden()
    {
        var doc = MakeDoc(
            findingsCount: 2,
            models: new[] { "claude-opus-4.7" },
            asymmetry: new AsymmetryStats { BothFound = 0, OpusOnly = 2, GptOnly = 0 });

        var badge = ConvergenceVerdict.Compute(doc);

        Assert.Equal(ConvergenceState.Hidden, badge.State);
    }

    [Fact]
    public void Compute_NoAsymmetryBlock_Hidden()
    {
        // Without an asymmetry block we can't make a convergence claim
        // even if there are 2 models declared.
        var doc = MakeDoc(
            findingsCount: 2,
            models: new[] { "claude-opus-4.7", "gpt-5.5" },
            asymmetry: null);

        var badge = ConvergenceVerdict.Compute(doc);

        Assert.Equal(ConvergenceState.Hidden, badge.State);
    }

    [Fact]
    public void Compute_AsymmetryAllZero_Hidden()
    {
        // Findings exist but the asymmetry block reports nothing counted.
        // Treat as "no claim", not "converged" — would otherwise be a
        // misleading green badge.
        var doc = MakeDoc(
            findingsCount: 2,
            models: new[] { "claude-opus-4.7", "gpt-5.5" },
            asymmetry: new AsymmetryStats { BothFound = 0, OpusOnly = 0, GptOnly = 0 });

        var badge = ConvergenceVerdict.Compute(doc);

        Assert.Equal(ConvergenceState.Hidden, badge.State);
    }

    [Fact]
    public void Compute_BothFoundOnly_Converged()
    {
        var doc = MakeDoc(
            findingsCount: 3,
            models: new[] { "claude-opus-4.7", "gpt-5.5" },
            asymmetry: new AsymmetryStats { BothFound = 3, OpusOnly = 0, GptOnly = 0 });

        var badge = ConvergenceVerdict.Compute(doc);

        Assert.Equal(ConvergenceState.Converged, badge.State);
        Assert.Equal("✓✓", badge.Glyph);
        Assert.Equal("converged", badge.CssClass);
        Assert.Contains("Both reviewers flagged every finding", badge.Tooltip);
        Assert.Contains("3 agreed", badge.Tooltip);
        Assert.Contains("claude-opus-4.7", badge.Tooltip);
        Assert.Contains("gpt-5.5", badge.Tooltip);
    }

    [Fact]
    public void Compute_BothFoundOnly_NullSingleModelCounters_Converged()
    {
        // OpusOnly/GptOnly nullable -> null means "not computed" (i.e. zero
        // by our convention). With BothFound > 0 and both null, treat as
        // converged.
        var doc = MakeDoc(
            findingsCount: 1,
            models: new[] { "claude-opus-4.7", "gpt-5.5" },
            asymmetry: new AsymmetryStats { BothFound = 1, OpusOnly = null, GptOnly = null });

        var badge = ConvergenceVerdict.Compute(doc);

        Assert.Equal(ConvergenceState.Converged, badge.State);
    }

    [Fact]
    public void Compute_OpusOnly_Asymmetric()
    {
        var doc = MakeDoc(
            findingsCount: 2,
            models: new[] { "claude-opus-4.7", "gpt-5.5" },
            asymmetry: new AsymmetryStats { BothFound = 1, OpusOnly = 1, GptOnly = 0 });

        var badge = ConvergenceVerdict.Compute(doc);

        Assert.Equal(ConvergenceState.Asymmetric, badge.State);
        Assert.Equal("⚠", badge.Glyph);
        Assert.Equal("asymmetric", badge.CssClass);
        Assert.Contains("both: 1", badge.Tooltip);
        Assert.Contains("claude-opus-4.7 only: 1", badge.Tooltip);
        Assert.Contains("gpt-5.5 only: 0", badge.Tooltip);
    }

    [Fact]
    public void Compute_GptOnly_Asymmetric()
    {
        var doc = MakeDoc(
            findingsCount: 3,
            models: new[] { "claude-opus-4.7", "gpt-5.5" },
            asymmetry: new AsymmetryStats { BothFound = 1, OpusOnly = 0, GptOnly = 2 });

        var badge = ConvergenceVerdict.Compute(doc);

        Assert.Equal(ConvergenceState.Asymmetric, badge.State);
        Assert.Contains("gpt-5.5 only: 2", badge.Tooltip);
    }

    [Fact]
    public void Compute_PureSingleModelDisagreement_Asymmetric()
    {
        // Worst case: zero overlap. Both models flagged things but none
        // agreed. The strongest "be careful" signal.
        var doc = MakeDoc(
            findingsCount: 4,
            models: new[] { "claude-opus-4.7", "gpt-5.5" },
            asymmetry: new AsymmetryStats { BothFound = 0, OpusOnly = 2, GptOnly = 2 });

        var badge = ConvergenceVerdict.Compute(doc);

        Assert.Equal(ConvergenceState.Asymmetric, badge.State);
        Assert.Contains("both: 0", badge.Tooltip);
    }

    [Fact]
    public void Compute_AsymmetricGenericLabels_WhenModelCountNotTwo()
    {
        // 3+ models: we don't know which two map to "opus_only" /
        // "gpt_only", so fall back to generic labels rather than guess.
        var doc = MakeDoc(
            findingsCount: 2,
            models: new[] { "claude-opus-4.7", "gpt-5.5", "gemini-2.0" },
            asymmetry: new AsymmetryStats { BothFound = 1, OpusOnly = 1, GptOnly = 0 });

        var badge = ConvergenceVerdict.Compute(doc);

        Assert.Equal(ConvergenceState.Asymmetric, badge.State);
        Assert.Contains("Opus only", badge.Tooltip);
        Assert.Contains("GPT only", badge.Tooltip);
    }
}
