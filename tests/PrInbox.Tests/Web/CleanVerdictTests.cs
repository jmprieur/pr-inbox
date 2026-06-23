using PrInbox.Core.Findings;
using PrInbox.Web.Services;

namespace PrInbox.Tests.Web;

/// <summary>
/// Tests for <see cref="CleanVerdict"/>. The verdict drives the inbox-row
/// tooltip and the Review-page green callout, so it has to handle the full
/// matrix: single reviewer, multi-reviewer-with-asymmetry, multi-reviewer-
/// without-asymmetry, and the no-models edge case.
/// </summary>
public class CleanVerdictTests
{
    private static readonly DateTimeOffset _now =
        new(2026, 5, 18, 4, 0, 0, TimeSpan.Zero);

    private static FindingsDocument MakeDoc(
        IReadOnlyList<string>? models = null,
        AsymmetryStats? asymmetry = null,
        TimeSpan? age = null)
    {
        return new FindingsDocument
        {
            Models = models ?? Array.Empty<string>(),
            Asymmetry = asymmetry,
            GeneratedAtUtc = _now - (age ?? TimeSpan.FromHours(2)),
            Findings = Array.Empty<Finding>(),
        };
    }

    [Fact]
    public void IsConverged_TwoModels_AllAsymmetryZero_True()
    {
        var doc = MakeDoc(
            models: new[] { "claude-opus-4.8", "gpt-5.5" },
            asymmetry: new AsymmetryStats { BothFound = 0, OpusOnly = 0, GptOnly = 0 });

        Assert.True(CleanVerdict.IsConverged(doc));
    }

    [Fact]
    public void IsConverged_TwoModels_NullAsymmetry_False()
    {
        // Older findings.yaml might not emit the asymmetry block. Without
        // it we can't assert agreement — the tooltip should still mention
        // "2 reviewers" but NOT "agree".
        var doc = MakeDoc(
            models: new[] { "claude-opus-4.8", "gpt-5.5" },
            asymmetry: null);

        Assert.False(CleanVerdict.IsConverged(doc));
    }

    [Fact]
    public void IsConverged_SingleModel_False()
    {
        var doc = MakeDoc(
            models: new[] { "claude-opus-4.8" },
            asymmetry: new AsymmetryStats { BothFound = 0, OpusOnly = 0, GptOnly = 0 });

        Assert.False(CleanVerdict.IsConverged(doc));
    }

    [Fact]
    public void IsConverged_BothFoundNonZero_False()
    {
        // Defensive: if a future schema lets both_found > 0 sit on a
        // zero-findings doc (e.g. agent records dropped findings), don't
        // claim convergence.
        var doc = MakeDoc(
            models: new[] { "claude-opus-4.8", "gpt-5.5" },
            asymmetry: new AsymmetryStats { BothFound = 1, OpusOnly = 0, GptOnly = 0 });

        Assert.False(CleanVerdict.IsConverged(doc));
    }

    [Fact]
    public void BuildTooltip_Converged_TwoReviewers_FullVerdict()
    {
        var doc = MakeDoc(
            models: new[] { "claude-opus-4.8", "gpt-5.5" },
            asymmetry: new AsymmetryStats { BothFound = 0, OpusOnly = 0, GptOnly = 0 },
            age: TimeSpan.FromHours(2));

        var s = CleanVerdict.BuildTooltip(doc, _now);

        Assert.Equal(
            "Reviewed clean 2h ago · 2 reviewers agree · claude-opus-4.8 + gpt-5.5",
            s);
    }

    [Fact]
    public void BuildTooltip_TwoReviewers_NoAsymmetry_NoAgreeWord()
    {
        // No asymmetry block → mention "2 reviewers" but NOT "agree".
        var doc = MakeDoc(
            models: new[] { "claude-opus-4.8", "gpt-5.5" },
            asymmetry: null,
            age: TimeSpan.FromMinutes(45));

        var s = CleanVerdict.BuildTooltip(doc, _now);

        Assert.Equal(
            "Reviewed clean 45m ago · 2 reviewers · claude-opus-4.8 + gpt-5.5",
            s);
    }

    [Fact]
    public void BuildTooltip_SingleReviewer_NoAgreeWord()
    {
        var doc = MakeDoc(
            models: new[] { "claude-opus-4.8" },
            asymmetry: null,
            age: TimeSpan.FromMinutes(30));

        var s = CleanVerdict.BuildTooltip(doc, _now);

        Assert.Equal("Reviewed clean 30m ago · claude-opus-4.8", s);
    }

    [Fact]
    public void BuildTooltip_NoModelsDeclared_TimeOnly()
    {
        var doc = MakeDoc(
            models: Array.Empty<string>(),
            asymmetry: null,
            age: TimeSpan.FromSeconds(15));

        var s = CleanVerdict.BuildTooltip(doc, _now);

        Assert.Equal("Reviewed clean just now", s);
    }

    [Fact]
    public void BuildTooltip_DaysAgo_RendersDayUnit()
    {
        var doc = MakeDoc(
            models: new[] { "claude-opus-4.8" },
            age: TimeSpan.FromDays(3));

        var s = CleanVerdict.BuildTooltip(doc, _now);

        Assert.Equal("Reviewed clean 3d ago · claude-opus-4.8", s);
    }

    [Fact]
    public void BuildTooltip_ThreeOrMoreModels_CountReflectsModelsCount()
    {
        // Forward-compat: if/when the dual-model protocol becomes triple-
        // model, the count should track Models.Count, not a hardcoded 2.
        var doc = MakeDoc(
            models: new[] { "claude-opus-4.8", "gpt-5.5", "gemini-3.0" },
            asymmetry: new AsymmetryStats { BothFound = 0, OpusOnly = 0, GptOnly = 0 },
            age: TimeSpan.FromHours(1));

        var s = CleanVerdict.BuildTooltip(doc, _now);

        Assert.Equal(
            "Reviewed clean 1h ago · 3 reviewers agree · claude-opus-4.8 + gpt-5.5 + gemini-3.0",
            s);
    }
}
