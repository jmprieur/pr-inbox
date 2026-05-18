using System.Collections.Concurrent;

namespace PrInbox.Web.Services;

/// <summary>
/// One tracked console window, identified by a unique title token (the
/// review's runId) and the HWND we discovered for it.
/// </summary>
public sealed record ConsoleWindowEntry(
    long RunId,
    string TitleToken,
    string DisplayTitle,
    IntPtr Hwnd,
    uint OwnerPid,
    DateTimeOffset LaunchedAt)
{
    /// <summary>True when <see cref="WindowInterop.IsWindow"/> still
    /// returns true AND the owning process id still matches the one we
    /// captured at registration. Anchoring to PID (not title) guards
    /// against HWND recycling while remaining immune to the running
    /// process rewriting its own window/tab title — which Windows
    /// Terminal review agents routinely do.</summary>
    public bool IsAlive()
    {
        if (Hwnd == IntPtr.Zero) return false;
        if (!OperatingSystem.IsWindows()) return false;
        if (!WindowInterop.IsWindow(Hwnd)) return false;
        var nowPid = WindowInterop.GetOwningProcessId(Hwnd);
        return nowPid != 0 && nowPid == OwnerPid;
    }

    /// <summary>True when the window is currently visible AND not minimized.</summary>
    public bool IsVisible()
    {
        if (!IsAlive()) return false;
        return WindowInterop.IsWindowVisible(Hwnd) && !WindowInterop.IsIconic(Hwnd);
    }
}

/// <summary>
/// Tracks console windows spawned by <see cref="ReviewLauncher"/> so the
/// inbox UI can show/minimize them after the fact.
///
/// Design notes:
/// <list type="bullet">
///   <item>Every launch embeds a unique <c>pr-inbox:run-{runId}</c> token in
///         the wt tab title. We poll <see cref="WindowInterop.FindByTitleToken"/>
///         in the background for up to 10 seconds after spawn.</item>
///   <item>The token is used ONLY at discovery time (the only place we have
///         a name-based way to identify which window is ours). After we have
///         the HWND, every <see cref="Toggle"/> call re-validates that the
///         HWND still maps to the same owner process id we recorded at
///         registration. This defeats HWND recycling — if Windows handed our
///         cached HWND to an unrelated window the PID will differ. The title
///         is NOT used for revalidation because review agents routinely
///         rewrite their own window/tab title at runtime.</item>
///   <item>"Hide" uses <see cref="WindowInterop.SW_MINIMIZE"/> rather than
///         <see cref="WindowInterop.SW_HIDE"/> so a pr-inbox crash leaves
///         the console reachable via the taskbar.</item>
///   <item>On shutdown <see cref="RestoreAll"/> SW_RESTOREs every tracked
///         window — belt-and-braces for the orphan-console problem.</item>
/// </list>
/// </summary>
public sealed class ConsoleWindowRegistry : IDisposable
{
    private readonly ILogger<ConsoleWindowRegistry> _log;
    private readonly ConcurrentDictionary<long, ConsoleWindowEntry> _entries = new();

    /// <summary>Fires when an entry is added or removed. The handler should
    /// be cheap and re-entrant — it runs on whichever thread mutated the
    /// registry (UI request thread or background poll thread).</summary>
    public event Action? Changed;

    public ConsoleWindowRegistry(ILogger<ConsoleWindowRegistry> log)
    {
        _log = log;
    }

    /// <summary>
    /// Stable token to embed in the wt tab title for a given run id. The
    /// launcher inserts the return value into the title; the registry's
    /// background poller finds the window by scanning <c>EnumWindows</c>
    /// for any title containing this token.
    /// </summary>
    public static string TokenFor(long runId) => $"pr-inbox:run-{runId}";

    /// <summary>
    /// Fire-and-forget. Background-polls <c>EnumWindows</c> for up to
    /// ~10 seconds looking for a window whose title contains
    /// <c>TokenFor(runId)</c>; saves the entry on hit. Misses are logged
    /// at Debug — the launch still succeeds, the registry just has no
    /// entry to toggle. Caller does not await.
    /// </summary>
    public void RegisterInBackground(long runId, string displayTitle)
    {
        if (!OperatingSystem.IsWindows())
        {
            _log.LogDebug("Skipping console registration: not on Windows.");
            return;
        }

        var token = TokenFor(runId);
        var launchedAt = DateTimeOffset.UtcNow;

        _ = Task.Run(async () =>
        {
            // Backoff: 200, 300, 450, 700, 1050, 1500, 2000, 2000, 2000
            // (≈10s total). wt cold start can exceed 5s on slow boxes.
            int[] delaysMs = { 200, 300, 450, 700, 1050, 1500, 2000, 2000, 2000 };
            foreach (var delay in delaysMs)
            {
                try
                {
                    var hwnd = WindowInterop.FindByTitleToken(token);
                    if (hwnd != IntPtr.Zero)
                    {
                        var ownerPid = WindowInterop.GetOwningProcessId(hwnd);
                        if (ownerPid == 0)
                        {
                            _log.LogDebug("Found hwnd for run {RunId} but PID lookup failed; will retry.", runId);
                        }
                        else
                        {
                            var entry = new ConsoleWindowEntry(
                                RunId: runId,
                                TitleToken: token,
                                DisplayTitle: displayTitle,
                                Hwnd: hwnd,
                                OwnerPid: ownerPid,
                                LaunchedAt: launchedAt);
                            _entries[runId] = entry;
                            _log.LogInformation("Tracked console for run {RunId} (hwnd={Hwnd}, pid={Pid}).", runId, hwnd.ToInt64(), ownerPid);
                            try { Changed?.Invoke(); } catch { }
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Console-registration poll failed for run {RunId}.", runId);
                }
                await Task.Delay(delay);
            }
            _log.LogDebug("Console for run {RunId} not found after polling. Toggle will be unavailable.", runId);
        });
    }

    /// <summary>
    /// Returns every still-alive entry. Dead entries (window closed by user
    /// OR HWND now belongs to a different window) are removed in-place and
    /// fire a single <see cref="Changed"/> event when at least one removal
    /// happened.
    /// </summary>
    public IReadOnlyList<ConsoleWindowEntry> List()
    {
        var alive = new List<ConsoleWindowEntry>();
        var removed = 0;
        foreach (var (runId, entry) in _entries.ToArray())
        {
            if (entry.IsAlive())
            {
                alive.Add(entry);
            }
            else if (_entries.TryRemove(runId, out _))
            {
                removed++;
            }
        }
        if (removed > 0)
        {
            try { Changed?.Invoke(); } catch { }
        }
        return alive
            .OrderByDescending(e => e.LaunchedAt)
            .ToList();
    }

    /// <summary>
    /// Show (SW_RESTORE) or hide (SW_MINIMIZE) a single tracked console
    /// by runId. Returns false when the entry is unknown / stale.
    /// Re-validates the HWND against the title token before acting.
    /// </summary>
    public bool Toggle(long runId, bool show)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (!_entries.TryGetValue(runId, out var entry)) return false;
        if (!entry.IsAlive())
        {
            _entries.TryRemove(runId, out _);
            return false;
        }
        var cmd = show ? WindowInterop.SW_RESTORE : WindowInterop.SW_MINIMIZE;
        return WindowInterop.ShowWindow(entry.Hwnd, cmd);
    }

    /// <summary>Apply <see cref="Toggle"/> to every alive entry.</summary>
    public int ToggleAll(bool show)
    {
        var n = 0;
        foreach (var entry in List())
        {
            if (Toggle(entry.RunId, show)) n++;
        }
        return n;
    }

    /// <summary>
    /// On shutdown, restore every tracked console so the user never loses a
    /// hidden window because pr-inbox exited.
    /// </summary>
    public void RestoreAll()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var entry in _entries.Values.ToArray())
        {
            if (entry.IsAlive())
            {
                try { WindowInterop.ShowWindowAsync(entry.Hwnd, WindowInterop.SW_RESTORE); } catch { }
            }
        }
    }

    public void Dispose() => RestoreAll();
}
