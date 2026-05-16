using System.Collections.Concurrent;
using PrInbox.Core.Findings;

namespace PrInbox.Web.Services;

/// <summary>
/// One review's runtime state in the web companion: the on-disk run
/// directory, the PR url, and -- once findings.yaml is written --
/// the parsed findings document plus a severity histogram.
/// </summary>
public sealed record ReviewRun(
    long RunId,
    string PrUrl,
    string RunDirectory,
    string HeadSha,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FindingsAtUtc,
    FindingsDocument? Findings,
    IReadOnlyList<string> FindingsErrors)
{
    public int CriticalCount => CountBy(FindingSeverity.Critical);
    public int HighCount     => CountBy(FindingSeverity.High);
    public int MediumCount   => CountBy(FindingSeverity.Medium);
    public int LowCount      => CountBy(FindingSeverity.Low);

    private int CountBy(FindingSeverity s)
        => Findings?.Findings.Count(f => f.Severity == s) ?? 0;
}

/// <summary>
/// Thread-safe registry of active and completed review runs. Keyed by
/// PR URL: a PR has at most one "current" run in the UI at any time;
/// re-running replaces the entry.
/// </summary>
public sealed class ReviewRunStore
{
    private readonly ConcurrentDictionary<string, ReviewRun> _byPrUrl = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    public IReadOnlyDictionary<string, ReviewRun> Snapshot()
        => new Dictionary<string, ReviewRun>(_byPrUrl, StringComparer.OrdinalIgnoreCase);

    public ReviewRun? Get(string prUrl)
        => _byPrUrl.TryGetValue(prUrl, out var r) ? r : null;

    public void StartedRun(ReviewRun run)
    {
        _byPrUrl[run.PrUrl] = run;
        Raise();
    }

    public void UpdateFindings(string prUrl, FindingsDocument? doc, IReadOnlyList<string> errors)
    {
        _byPrUrl.AddOrUpdate(prUrl,
            _ => throw new InvalidOperationException("No run for url"),
            (_, prev) => prev with
            {
                Findings = doc,
                FindingsErrors = errors,
                FindingsAtUtc = DateTimeOffset.UtcNow,
            });
        Raise();
    }

    private void Raise()
    {
        try { Changed?.Invoke(); }
        catch { /* swallow subscriber exceptions */ }
    }
}
