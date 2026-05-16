namespace PrInbox.Core.Models;

/// <summary>
/// Per-PR enrichment state managed by the progressive-fetch pipeline.
/// </summary>
/// <remarks>
/// <para>
/// A row enters <see cref="Basic"/> when fast-tier sync first sees the PR
/// (URL, title, dates) and again whenever a fast-tier sync detects upstream
/// change. It moves to <see cref="Enriched"/> when tier-3 enrichment has
/// fetched per-PR detail (head/base SHAs, commits, reviewer state) and
/// threads (comments, reviews) and persisted them.
/// </para>
/// <para>
/// Stored in <c>pull_requests.enrich_state</c> as a lowercase string.
/// </para>
/// </remarks>
public enum EnrichState
{
    /// <summary>Listed but not yet enriched; snapshot+threads may be missing or stale.</summary>
    Basic,

    /// <summary>Fully enriched: detail and threads have been fetched and persisted.</summary>
    Enriched,
}

/// <summary>
/// SQL value mapping for <see cref="EnrichState"/>.
/// </summary>
public static class EnrichStateExtensions
{
    public static string ToDbValue(this EnrichState state) => state switch
    {
        EnrichState.Basic => "basic",
        EnrichState.Enriched => "enriched",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    public static EnrichState FromDbValue(string value) => value switch
    {
        "basic" => EnrichState.Basic,
        "enriched" => EnrichState.Enriched,
        _ => throw new InvalidOperationException($"Unknown enrich_state '{value}'."),
    };
}
