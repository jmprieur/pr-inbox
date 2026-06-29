using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
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

    /// <summary>
    /// Per-finding body overrides keyed by <see cref="Finding.Id"/>.
    /// Layered on top of the parsed body at post time; an absent key
    /// means "use the agent's prose verbatim". Persisted to
    /// <c>comment-overrides.json</c> in the run directory by
    /// <see cref="ReviewRunStore.SetBodyOverride"/> so edits survive a
    /// web restart or a benign reparse of <c>findings.yaml</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> BodyOverrides { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

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
        // Resume any prior comment-overrides we wrote for the same run dir
        // (web restart, page reload, etc.) so the user's edits don't go
        // missing across process boundaries.
        if (run.BodyOverrides.Count == 0)
        {
            var fromDisk = ReadOverridesFromDisk(run.RunDirectory);
            if (fromDisk.Count > 0)
            {
                run = run with { BodyOverrides = fromDisk };
            }
        }
        _byPrUrl[run.PrUrl] = run;
        Raise();
    }

    public void UpdateFindings(string prUrl, FindingsDocument? doc, IReadOnlyList<string> errors)
    {
        // Defense-in-depth: findings.yaml is written by an LLM agent that
        // reads attacker-controlled diff content. If the agent reports a
        // pr_url that differs from the trusted run key, surface it as an
        // error so Review.razor renders the warning banner instead of
        // silently posting wrong-PR findings under the user's name. This
        // is NOT a write-redirect (publish always uses the trusted prUrl
        // key), purely an off-rails canary.
        if (doc is { PrUrl: { Length: > 0 } reported }
            && !string.Equals(reported, prUrl, StringComparison.OrdinalIgnoreCase))
        {
            errors = [.. errors,
                $"findings.yaml pr_url '{reported}' does not match this run's PR '{prUrl}'. " +
                "The agent may have reviewed the wrong PR — verify before publishing."];
        }

        // NOTE: the `with` clause intentionally leaves BodyOverrides alone.
        // FindingsWatcher can reparse findings.yaml several times during a
        // single run (the agent re-writes it as it works); the user's
        // inline edits must survive those reparses.
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

    /// <summary>
    /// Set or replace a body override for one finding and persist to disk.
    /// Caller is responsible for empty/whitespace and equality-to-original
    /// semantics -- this method always stores whatever it's given. Use
    /// <see cref="ClearBodyOverride"/> to remove an override.
    /// </summary>
    public void SetBodyOverride(string prUrl, string findingId, string body)
    {
        if (string.IsNullOrEmpty(findingId)) return;
        ReviewRun? after = null;
        _byPrUrl.AddOrUpdate(prUrl,
            _ => throw new InvalidOperationException($"No run for url {prUrl}"),
            (_, prev) =>
            {
                var dict = new Dictionary<string, string>(prev.BodyOverrides, StringComparer.Ordinal)
                {
                    [findingId] = body,
                };
                after = prev with { BodyOverrides = dict };
                return after;
            });
        if (after is not null)
        {
            WriteOverridesToDisk(after.RunDirectory, after.BodyOverrides);
        }
        Raise();
    }

    /// <summary>Remove an existing override (no-op if absent).</summary>
    public void ClearBodyOverride(string prUrl, string findingId)
    {
        if (string.IsNullOrEmpty(findingId)) return;
        ReviewRun? after = null;
        _byPrUrl.AddOrUpdate(prUrl,
            _ => throw new InvalidOperationException($"No run for url {prUrl}"),
            (_, prev) =>
            {
                if (!prev.BodyOverrides.ContainsKey(findingId)) return prev;
                var dict = new Dictionary<string, string>(prev.BodyOverrides, StringComparer.Ordinal);
                dict.Remove(findingId);
                after = prev with { BodyOverrides = dict };
                return after;
            });
        if (after is not null)
        {
            WriteOverridesToDisk(after.RunDirectory, after.BodyOverrides);
        }
        Raise();
    }

    /// <summary>Filename used inside a run directory.</summary>
    internal const string OverridesFileName = "comment-overrides.json";

    private static readonly JsonSerializerOptions OverridesJson = new()
    {
        WriteIndented = true,
    };

    private static void WriteOverridesToDisk(string runDir, IReadOnlyDictionary<string, string> overrides)
    {
        var path = Path.Combine(runDir, OverridesFileName);
        try
        {
            if (overrides.Count == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            // Sort keys so the file diffs cleanly across edits.
            var sorted = overrides.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                                  .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            var json = JsonSerializer.Serialize(sorted, OverridesJson);
            File.WriteAllText(path, json);
        }
        catch
        {
            // Best-effort persistence. The in-memory dict is authoritative
            // for the current session; losing the file just means a future
            // session won't see these edits.
        }
    }

    internal static IReadOnlyDictionary<string, string> ReadOverridesFromDisk(string runDir)
    {
        var path = Path.Combine(runDir, OverridesFileName);
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        try
        {
            var json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return parsed is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(parsed, StringComparer.Ordinal);
        }
        catch
        {
            // Corrupt file -- ignore, treat as no overrides. Don't delete
            // it; let the user inspect/repair if they care.
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void Raise()
    {
        try { Changed?.Invoke(); }
        catch { /* swallow subscriber exceptions */ }
    }
}
