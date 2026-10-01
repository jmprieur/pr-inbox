namespace PrInbox.Core.Findings;

/// <summary>
/// Top-level findings document produced by <c>dual-model-review</c> and
/// consumed by pr-inbox-web. Schema v1.
/// </summary>
public sealed record FindingsDocument
{
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Canonical PR URL the review targets.</summary>
    public string PrUrl { get; init; } = string.Empty;

    /// <summary>Platform stable identity (rename-safe). Optional but recommended.</summary>
    public string? PrStableIdentity { get; init; }

    /// <summary>HEAD SHA the review ran against; used for staleness checks before posting.</summary>
    public string HeadSha { get; init; } = string.Empty;

    public string? BaseSha { get; init; }

    public DateTimeOffset GeneratedAtUtc { get; init; }

    /// <summary>
    /// Legacy combined model list. New producers should use
    /// <see cref="PrimaryModels"/> and <see cref="ShadowModels"/>.
    /// </summary>
    public IReadOnlyList<string> Models { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The Opus + GPT reviewers whose independent reports determine findings and asymmetry.
    /// </summary>
    public IReadOnlyList<string> PrimaryModels { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Supplemental reviewers whose output is informational and never counts toward quorum.
    /// </summary>
    public IReadOnlyList<string> ShadowModels { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Completeness of the primary review pair. Null means a legacy schema-v1 document.
    /// </summary>
    public ReviewCompleteness? ReviewStatus { get; init; }

    /// <summary>Required explanation when <see cref="ReviewStatus"/> is incomplete.</summary>
    public string? IncompleteReason { get; init; }

    /// <summary>Primary models, falling back to the legacy combined list.</summary>
    public IReadOnlyList<string> EffectivePrimaryModels =>
        PrimaryModels.Count > 0 ? PrimaryModels : Models;

    /// <summary>Diagnostic breakdown of model agreement; free-shape map for forward compatibility.</summary>
    public AsymmetryStats? Asymmetry { get; init; }

    public IReadOnlyList<Finding> Findings { get; init; } = Array.Empty<Finding>();
}

/// <summary>Model-asymmetry diagnostics. Numbers may be null if not computed.</summary>
public sealed record AsymmetryStats
{
    public int BothFound { get; init; }
    public int? OpusOnly { get; init; }
    public int? GptOnly { get; init; }
    public int? SingleModel { get; init; }
}

/// <summary>One actionable finding in the document.</summary>
public sealed record Finding
{
    /// <summary>Stable per-document id, e.g. <c>f01</c>. Used by pr-inbox-web to track posted state.</summary>
    public string Id { get; init; } = string.Empty;

    public FindingSeverity Severity { get; init; } = FindingSeverity.Medium;

    public FindingConfidence Confidence { get; init; } = FindingConfidence.Medium;

    /// <summary>Which model(s) reported this finding (informational; uniqueness not enforced).</summary>
    public IReadOnlyList<string> FoundBy { get; init; } = Array.Empty<string>();

    /// <summary>Repo-relative file path.</summary>
    public string File { get; init; } = string.Empty;

    public int? Line { get; init; }

    public int? LineEnd { get; init; }

    /// <summary>True when (file, line) sits inside the diff and is safe to post inline.</summary>
    public bool DiffAnchorable { get; init; }

    /// <summary>One-line summary; renders as the finding header.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Multi-line markdown explanation. The body posted as the PR comment.</summary>
    public string? Body { get; init; }

    /// <summary>Optional GitHub <c>```suggestion</c> code block, including the fence.</summary>
    public string? SuggestedInline { get; init; }
}

public enum FindingSeverity
{
    Critical,
    High,
    Medium,
    Low,
}

public enum FindingConfidence
{
    High,
    Medium,
    Low,
}

public enum ReviewCompleteness
{
    Complete,
    Degraded,
    Incomplete,
}

/// <summary>
/// Helpers for converting <see cref="FindingSeverity"/> and
/// <see cref="FindingConfidence"/> to/from the lowercase string form used
/// in <c>findings.yaml</c>.
/// </summary>
public static class FindingEnumExtensions
{
    public static string ToYamlValue(this FindingSeverity s) => s switch
    {
        FindingSeverity.Critical => "critical",
        FindingSeverity.High => "high",
        FindingSeverity.Medium => "medium",
        FindingSeverity.Low => "low",
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, null),
    };

    public static string ToYamlValue(this FindingConfidence c) => c switch
    {
        FindingConfidence.High => "high",
        FindingConfidence.Medium => "medium",
        FindingConfidence.Low => "low",
        _ => throw new ArgumentOutOfRangeException(nameof(c), c, null),
    };

    public static FindingSeverity ParseSeverity(string value) => value?.ToLowerInvariant() switch
    {
        "critical" => FindingSeverity.Critical,
        "high" => FindingSeverity.High,
        "medium" => FindingSeverity.Medium,
        "low" => FindingSeverity.Low,
        _ => throw new FormatException($"Unknown finding severity '{value}'."),
    };

    public static FindingConfidence ParseConfidence(string value) => value?.ToLowerInvariant() switch
    {
        "high" => FindingConfidence.High,
        "medium" => FindingConfidence.Medium,
        "low" => FindingConfidence.Low,
        _ => throw new FormatException($"Unknown finding confidence '{value}'."),
    };

    public static string ToYamlValue(this ReviewCompleteness status) => status switch
    {
        ReviewCompleteness.Complete => "complete",
        ReviewCompleteness.Degraded => "degraded",
        ReviewCompleteness.Incomplete => "incomplete",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static ReviewCompleteness ParseReviewCompleteness(string value) => value?.ToLowerInvariant() switch
    {
        "complete" => ReviewCompleteness.Complete,
        "degraded" => ReviewCompleteness.Degraded,
        "incomplete" => ReviewCompleteness.Incomplete,
        _ => throw new FormatException($"Unknown review status '{value}'."),
    };
}
