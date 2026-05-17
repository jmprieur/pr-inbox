using System.Collections.Concurrent;
using System.Diagnostics;
using PrInbox.Core.Credentials;
using PrInbox.Core.Reviewing;
using PrInbox.Core.Storage;

namespace PrInbox.Web.Services;

/// <summary>
/// Abstraction over launching a dual-model-review session in a new
/// console window for a given PR.
/// </summary>
public interface IReviewLauncher
{
    /// <summary>
    /// Build the brief, start a console window, and attach a watcher
    /// for the resulting <c>findings.yaml</c>.
    /// </summary>
    /// <returns>A short user-visible message describing what happened.</returns>
    Task<string> LaunchAsync(string prUrl, CancellationToken ct);
}

/// <summary>
/// Production review launcher.
/// <list type="number">
///   <item>Calls <see cref="BriefService"/> to generate brief.md + metadata + review_runs row.</item>
///   <item>Spawns a new Windows Terminal window running <c>launch-review.ps1</c>
///         with the run directory.</item>
///   <item>Starts a <see cref="FindingsWatcher"/> on the run dir so the
///         inbox lights up the moment <c>findings.yaml</c> appears.</item>
/// </list>
/// Falls back gracefully when <c>wt.exe</c> is not on PATH: spawns
/// <c>pwsh.exe</c> directly in a new window via cmd /c start.
/// </summary>
public sealed class ReviewLauncher : IReviewLauncher, IAsyncDisposable
{
    private readonly ReviewRunStore _runs;
    private readonly ILogger<ReviewLauncher> _log;
    private readonly ILoggerFactory _logFactory;
    private readonly PrInboxConfig _config;
    private readonly ConcurrentDictionary<string, FindingsWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);

    public ReviewLauncher(ReviewRunStore runs, ILogger<ReviewLauncher> log, ILoggerFactory logFactory, PrInboxConfig config)
    {
        _runs = runs;
        _log = log;
        _logFactory = logFactory;
        _config = config;
    }

    public async Task<string> LaunchAsync(string prUrl, CancellationToken ct)
    {
        var (prRepo, snapRepo, threadRepo, reviewRepo) = OpenRepos();
        var briefService = new BriefService(prRepo, snapRepo, threadRepo, reviewRepo);

        BriefResult brief;
        try
        {
            brief = await briefService.CreateBriefAsync(prUrl, ct);
        }
        catch (BriefCreationException ex)
        {
            return ex.Message;
        }

        // Build a "<repo> #<number> @<short-sha> <HH:mm>" title for the wt
        // tab and the underlying agent's session name. Including the short
        // HEAD SHA *and* a minute-resolution timestamp guarantees each launch
        // claims a fresh copilot session — without the timestamp, copilot
        // refuses to start a session whose name collides with an existing
        // one (it tries to auto-resume and then errors because --name and
        // --resume are mutually exclusive). The HH:mm suffix is short and
        // human-readable so the --resume picker stays scannable.
        string tabTitle = "pr-inbox review";
        try
        {
            var row = await prRepo.GetAsync(brief.PrUrl, ct);
            if (row is not null)
            {
                var shortSha = brief.HeadSha.Length >= 7 ? brief.HeadSha[..7] : brief.HeadSha;
                var hhmm = DateTimeOffset.Now.ToString("HH:mm");
                tabTitle = $"{row.DisplayRepo} #{row.Number} @{shortSha} {hhmm}";
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not resolve repo/number for {Url}; using default tab title.", brief.PrUrl);
        }

        StartWatcher(brief.PrUrl, brief.RunDirectory, brief.RunId, brief.HeadSha);
        SpawnConsole(brief.RunDirectory, tabTitle);

        return $"Review run #{brief.RunId} opened in a new window. Findings will land in {brief.RunDirectory}\\findings.yaml.";
    }

    /// <summary>
    /// Re-attach watchers to every run directory containing a brief but no
    /// findings yet. Called once at startup so a web restart doesn't lose
    /// visibility on in-flight reviews.
    /// </summary>
    public void RehydrateInFlightRuns()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var reviewsRoot = Path.Combine(appData, "PrInbox", "reviews");
            if (!Directory.Exists(reviewsRoot)) return;

            var (prRepo, _, _, reviewRepo) = OpenRepos();
            var latestByPr = new Dictionary<string, (string runDir, long runId, string head, DateTimeOffset created)>(
                StringComparer.OrdinalIgnoreCase);

            // Walk: reviewsRoot/<safe-pr>/<ts>_<sha>/{brief.md, [findings.yaml]}
            foreach (var safePrDir in Directory.EnumerateDirectories(reviewsRoot))
            {
                foreach (var runDir in Directory.EnumerateDirectories(safePrDir))
                {
                    var briefPath = Path.Combine(runDir, "brief.md");
                    var metaPath = Path.Combine(runDir, "metadata.json");
                    if (!File.Exists(briefPath) || !File.Exists(metaPath)) continue;

                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(metaPath));
                        // Newer metadata.json uses pr_url; older revisions only had url.
                        string prUrl =
                            (doc.RootElement.TryGetProperty("pr_url", out var pu) ? pu.GetString() : null)
                            ?? (doc.RootElement.TryGetProperty("url", out var u) ? u.GetString() : null)
                            ?? "";
                        string head =
                            (doc.RootElement.TryGetProperty("head_sha", out var hs) ? hs.GetString() : null) ?? "";
                        if (string.IsNullOrEmpty(prUrl)) continue;
                        var created = Directory.GetCreationTimeUtc(runDir);

                        if (!latestByPr.TryGetValue(prUrl, out var existing) || created > existing.created)
                        {
                            latestByPr[prUrl] = (runDir, 0L, head, new DateTimeOffset(created, TimeSpan.Zero));
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogDebug(ex, "Skipping bad metadata at {Path}", metaPath);
                    }
                }
            }

            foreach (var (prUrl, info) in latestByPr)
            {
                StartWatcher(prUrl, info.runDir, info.runId, info.head, startedAtUtc: info.created);
            }
            _log.LogInformation("Rehydrated {Count} review run(s) from {Root}", latestByPr.Count, reviewsRoot);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Rehydration failed");
        }
    }

    private void StartWatcher(string prUrl, string runDir, long runId, string headSha,
        DateTimeOffset? startedAtUtc = null)
    {
        var run = new ReviewRun(
            RunId: runId,
            PrUrl: prUrl,
            RunDirectory: runDir,
            HeadSha: headSha,
            StartedAtUtc: startedAtUtc ?? DateTimeOffset.UtcNow,
            FindingsAtUtc: null,
            Findings: null,
            FindingsErrors: Array.Empty<string>());
        _runs.StartedRun(run);

        var watcher = new FindingsWatcher(prUrl, runDir, _runs, _logFactory.CreateLogger<FindingsWatcher>());
        // Dispose any previous watcher for the same PR before replacing.
        if (_watchers.TryRemove(prUrl, out var old))
        {
            try { old.Dispose(); } catch { }
        }
        _watchers[prUrl] = watcher;
    }

    private void SpawnConsole(string runDir, string tabTitle)
    {
        var ps1 = FindLauncherScript();
        if (ps1 is null)
        {
            _log.LogWarning("launch-review.ps1 not found; cannot spawn console.");
            return;
        }

        var rl = _config.ReviewLauncher;
        var mcps = string.Join(",", rl.AdditionalMcps ?? new());
        // Quote values defensively in case a user puts spaces in them. The
        // tab title is also re-used as the underlying agent's session name
        // (--name) so each review claims its own copilot session and the
        // CLI doesn't auto-load a colliding prior one.
        var safeSessionName = string.IsNullOrWhiteSpace(tabTitle)
            ? "pr-inbox review"
            : tabTitle.Replace("\"", "");
        var launcherArgs =
            $"-RunDirectory \"{runDir}\"" +
            $" -Plugin \"{rl.Plugin}\"" +
            $" -Model \"{rl.Model}\"" +
            $" -Agent \"{rl.Agent}\"" +
            $" -SessionName \"{safeSessionName}\"" +
            (string.IsNullOrEmpty(mcps) ? "" : $" -Mcps \"{mcps}\"");

        // Strip embedded quotes from the tab title so wt doesn't mis-parse
        // the command line. Falls back to the generic title on empty.
        var safeTitle = string.IsNullOrWhiteSpace(tabTitle)
            ? "pr-inbox review"
            : tabTitle.Replace("\"", "");

        var wt = ResolveOnPath("wt.exe");
        try
        {
            if (wt is not null)
            {
                var args = $"-w 0 nt --title \"{safeTitle}\" -d \"{runDir}\" pwsh -NoExit -File \"{ps1}\" {launcherArgs}";
                Process.Start(new ProcessStartInfo
                {
                    FileName = wt,
                    Arguments = args,
                    UseShellExecute = true,
                });
                return;
            }

            // Fallback: open a fresh pwsh window via cmd /c start. The
            // start title is used as the new window's title bar text.
            var fallbackArgs = $"/c start \"{safeTitle}\" pwsh -NoExit -File \"{ps1}\" {launcherArgs}";
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = fallbackArgs,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to spawn review console for {RunDir}", runDir);
        }
    }

    private static string? FindLauncherScript()
    {
        // 1. Environment override.
        var envPath = Environment.GetEnvironmentVariable("PRINBOX_LAUNCH_SCRIPT");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath)) return envPath;

        // 2. Walk up from the current binary towards a 'tools/launch-review.ps1'.
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "tools", "launch-review.ps1");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private static string? ResolveOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (path is null) return null;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed path entry */ }
        }
        return null;
    }

    private static (PullRequestRepository pr, PrSnapshotRepository snap,
                    ObservedThreadRepository threads, ReviewRunRepository runs) OpenRepos()
    {
        var db = new PrInboxDb(PrInboxDb.DefaultUserConnectionString());
        new MigrationRunner().MigrateAsync(db.ConnectionString).GetAwaiter().GetResult();
        return (new PullRequestRepository(db),
                new PrSnapshotRepository(db),
                new ObservedThreadRepository(db),
                new ReviewRunRepository(db));
    }

    public ValueTask DisposeAsync()
    {
        foreach (var w in _watchers.Values)
        {
            try { w.Dispose(); } catch { }
        }
        _watchers.Clear();
        return ValueTask.CompletedTask;
    }
}
