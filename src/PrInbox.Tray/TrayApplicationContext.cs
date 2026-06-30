using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PrInbox.Tray;

/// <summary>
/// The tray application: a NotifyIcon with a context menu that supervises the
/// hidden web server. No console window — the user opens the dashboard in their
/// browser and stops everything from the menu.
///
/// All lifecycle transitions (start, restart, stop) are serialized through a
/// single gate so menu clicks can't interleave into a half-torn-down state. A
/// Stop requested while startup is still polling for health cancels the poll so
/// the user never waits out the full health timeout.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly ToolStripMenuItem _openItem;
    private readonly ToolStripMenuItem _restartItem;
    private readonly System.Windows.Forms.Timer _bootstrap;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Self-healing tunables.
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(30);
    private const int WatchdogFailuresBeforeRestart = 2;
    private static readonly TimeSpan ResumeGrace = TimeSpan.FromSeconds(5);
    private const int MaxRecoveriesPerWindow = 3;
    private static readonly TimeSpan RecoveryWindow = TimeSpan.FromMinutes(10);
    private readonly List<DateTime> _recentRecoveries = new();

    private WebHostProcess _host;
    private CancellationTokenSource? _startupCts;
    private CancellationTokenSource? _watchdogCts;
    private SynchronizationContext? _ui;
    private volatile bool _exiting;
    private bool _running;
    private bool _restarting;

    public TrayApplicationContext()
    {
        _host = CreateHost();

        _icon = TrayIconFactory.Create();

        _openItem = new ToolStripMenuItem("Open PR Inbox", null, (_, _) => OpenBrowser())
        {
            Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold),
            Enabled = false,
        };
        _restartItem = new ToolStripMenuItem("Restart", null, async (_, _) => await RestartAsync())
        {
            Enabled = false,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_openItem);
        menu.Items.Add(_restartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("View log", null, (_, _) => OpenLog()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Stop && Exit", null, async (_, _) => await StopAndExitAsync()));

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "PR Inbox — starting…",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notifyIcon.DoubleClick += (_, _) => OpenBrowser();

        // Recover quickly when the machine wakes from sleep — a resumed session
        // can leave the web gone or wedged while this tray lives on.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // Defer startup until the message loop is running so the WinForms
        // SynchronizationContext is installed and await-continuations land on
        // the UI thread.
        _bootstrap = new System.Windows.Forms.Timer { Interval = 50 };
        _bootstrap.Tick += async (_, _) =>
        {
            _bootstrap.Stop();
            _ui = SynchronizationContext.Current;
            await RunStartupAsync();
        };
        _bootstrap.Start();
    }

    private WebHostProcess CreateHost()
    {
        var host = new WebHostProcess();
        host.ExitedUnexpectedly += OnWebExitedUnexpectedly;
        return host;
    }

    private async Task RunStartupAsync()
    {
        try
        {
            await _gate.WaitAsync();
            try
            {
                if (_exiting)
                {
                    return;
                }

                await StartCoreAsync();
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            TrayLog.Write($"RunStartupAsync error: {ex}");
        }
    }

    private async Task StartCoreAsync()
    {
        TrayLog.Write("StartCore: begin");
        try
        {
            _host.Start();
        }
        catch (Exception ex)
        {
            TrayLog.Write($"StartCore: Start() threw: {ex}");
            ShowBalloon("PR Inbox failed to start", ex.Message, ToolTipIcon.Error);
            _notifyIcon.Text = "PR Inbox — not running";
            _restartItem.Enabled = true;
            return;
        }

        _startupCts = new CancellationTokenSource();
        var healthy = await _host.WaitForHealthyAsync(TimeSpan.FromSeconds(40), _startupCts.Token);
        TrayLog.Write($"StartCore: healthy = {healthy}");

        if (_exiting)
        {
            return;
        }

        if (healthy)
        {
            _running = true;
            _openItem.Enabled = true;
            _restartItem.Enabled = true;
            _notifyIcon.Text = Truncate($"PR Inbox — {_host.BaseUrl}");
            ShowBalloon("PR Inbox is running", $"Listening on {_host.BaseUrl}. Click to open.", ToolTipIcon.Info);
            StartWatchdog();
            OpenBrowser();
        }
        else
        {
            _running = false;
            _openItem.Enabled = false;
            _restartItem.Enabled = true;
            _notifyIcon.Text = "PR Inbox — not responding";
            ShowBalloon(
                "PR Inbox didn't come up",
                "The server didn't respond in time. Use 'View log' for details, then Restart.",
                ToolTipIcon.Warning);
        }
    }

    private async Task RestartAsync(string? autoReason = null)
    {
        // Single-flight: ignore overlapping restart triggers (menu double-click,
        // watchdog, resume hook, unexpected-exit) so they can't stack.
        if (_restarting)
        {
            return;
        }
        _restarting = true;
        try
        {
            // Stop the watchdog before teardown so its health polls don't race
            // the restart and queue another recovery.
            StopWatchdog();

            await _gate.WaitAsync();
            try
            {
                if (_exiting)
                {
                    return;
                }

                _openItem.Enabled = false;
                _restartItem.Enabled = false;
                _running = false;
                _notifyIcon.Text = autoReason is null
                    ? "PR Inbox — restarting…"
                    : "PR Inbox — recovering…";
                if (autoReason is not null)
                {
                    TrayLog.Write($"Auto-recover: restarting because {autoReason}.");
                    ShowBalloon("PR Inbox recovering", $"Restarting because {autoReason}.", ToolTipIcon.Warning);
                }

                _host.ExitedUnexpectedly -= OnWebExitedUnexpectedly;
                await _host.StopAsync();
                _host.Dispose();

                if (_exiting)
                {
                    return;
                }

                _host = CreateHost();
                await StartCoreAsync();
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            TrayLog.Write($"RestartAsync error: {ex}");
        }
        finally
        {
            _restarting = false;
        }
    }

    // --- Self-healing: health watchdog + sleep/resume recovery ----------------

    /// <summary>Begins periodic /healthz polling. Safe to call repeatedly; any
    /// existing watchdog is cancelled first.</summary>
    private void StartWatchdog()
    {
        StopWatchdog();
        var cts = new CancellationTokenSource();
        _watchdogCts = cts;
        _ = Task.Run(() => WatchdogLoopAsync(cts.Token));
    }

    private void StopWatchdog()
    {
        try { _watchdogCts?.Cancel(); } catch { }
        _watchdogCts?.Dispose();
        _watchdogCts = null;
    }

    /// <summary>Polls /healthz on a fixed cadence while the server is supposed to
    /// be up. After enough consecutive failures it triggers an auto-restart —
    /// this is what catches a web that's gone or wedged while the tray lives on
    /// (e.g. after the machine sleeps and wakes, which the old build couldn't
    /// notice because it only watched for the child process exiting).</summary>
    private async Task WatchdogLoopAsync(CancellationToken ct)
    {
        var failures = 0;
        try
        {
            using var timer = new PeriodicTimer(WatchdogInterval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (_exiting || !_running)
                {
                    failures = 0;
                    continue;
                }

                bool healthy;
                try
                {
                    healthy = await _host.IsHealthyAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    healthy = false;
                }

                if (healthy)
                {
                    failures = 0;
                    continue;
                }

                failures++;
                TrayLog.Write($"Watchdog: health check failed ({failures}/{WatchdogFailuresBeforeRestart}).");
                if (failures >= WatchdogFailuresBeforeRestart)
                {
                    RequestAutoRecover("the web server stopped responding");
                    return; // the restart starts a fresh watchdog; end this loop.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled by StopWatchdog — normal.
        }
        catch (Exception ex)
        {
            TrayLog.Write($"Watchdog loop error: {ex}");
        }
    }

    /// <summary>Marshals an auto-restart onto the UI thread, rate-limited so a
    /// web that refuses to stay up can't spin into a restart storm.</summary>
    private void RequestAutoRecover(string reason)
    {
        void Run()
        {
            if (_exiting || _restarting)
            {
                return;
            }

            if (!AllowRecovery())
            {
                TrayLog.Write("Auto-recover suppressed: too many attempts in the recovery window.");
                _running = false;
                _openItem.Enabled = false;
                _restartItem.Enabled = true;
                _notifyIcon.Text = "PR Inbox — not responding";
                ShowBalloon(
                    "PR Inbox keeps stopping",
                    "Auto-recovery gave up after repeated attempts. Use 'View log', then Restart.",
                    ToolTipIcon.Error);
                return;
            }

            _ = RestartAsync(reason);
        }

        if (_ui is not null)
        {
            _ui.Post(_ => Run(), null);
        }
        else
        {
            Run();
        }
    }

    // UI-thread only — keeps the sliding window of recent auto-recoveries.
    private bool AllowRecovery()
    {
        var now = DateTime.UtcNow;
        _recentRecoveries.RemoveAll(t => now - t > RecoveryWindow);
        if (_recentRecoveries.Count >= MaxRecoveriesPerWindow)
        {
            return false;
        }
        _recentRecoveries.Add(now);
        return true;
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume)
        {
            return;
        }

        TrayLog.Write("PowerMode: Resume — scheduling a health check.");
        _ = Task.Run(async () =>
        {
            try
            {
                // Let the network stack and any pending OS work settle first.
                await Task.Delay(ResumeGrace).ConfigureAwait(false);
                if (_exiting || !_running)
                {
                    return;
                }

                bool healthy;
                try { healthy = await _host.IsHealthyAsync(CancellationToken.None).ConfigureAwait(false); }
                catch { healthy = false; }

                if (!healthy)
                {
                    RequestAutoRecover("the machine resumed from sleep and the web wasn't responding");
                }
            }
            catch (Exception ex)
            {
                TrayLog.Write($"Resume health check error: {ex}");
            }
        });
    }

    private async Task StopAndExitAsync()
    {
        try
        {
            if (_exiting)
            {
                return;
            }

            // Flip the flag and cancel any in-flight health poll before taking
            // the gate, so a Stop during startup interrupts promptly.
            _exiting = true;
            try { _startupCts?.Cancel(); } catch { }
            StopWatchdog();
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;

            _notifyIcon.Text = "PR Inbox — stopping…";
            _notifyIcon.Visible = false;

            await _gate.WaitAsync();
            try
            {
                _running = false;
                await _host.StopAsync();
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            TrayLog.Write($"StopAndExitAsync error: {ex}");
        }
        finally
        {
            ExitThread();
        }
    }

    private void OpenBrowser()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_host.BaseUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowBalloon("Couldn't open browser", ex.Message, ToolTipIcon.Error);
        }
    }

    private void OpenLog()
    {
        try
        {
            var path = File.Exists(_host.LogFilePath) ? _host.LogFilePath : TrayLog.Path;
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            else
            {
                ShowBalloon("No log yet", "No log has been written this session.", ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            ShowBalloon("Couldn't open log", ex.Message, ToolTipIcon.Error);
        }
    }

    private void OnWebExitedUnexpectedly()
    {
        // Raised on a thread-pool thread — marshal to the UI thread.
        void Handle()
        {
            // Only act if the server had actually come up; a child that dies
            // during the initial start sequence is reported by StartCore.
            if (_exiting || !_running || _restarting)
            {
                return;
            }

            _running = false;
            _openItem.Enabled = false;
            _restartItem.Enabled = true;
            _notifyIcon.Text = "PR Inbox — stopped";

            // The web process died on its own — bring it back (rate-limited).
            RequestAutoRecover("the web server exited");
        }

        if (_ui is not null)
        {
            _ui.Post(_ => Handle(), null);
        }
        else
        {
            Handle();
        }
    }

    private void ShowBalloon(string title, string text, ToolTipIcon icon)
    {
        if (!_notifyIcon.Visible)
        {
            return;
        }

        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.ShowBalloonTip(5000);
    }

    private static string Truncate(string s) => s.Length <= 63 ? s : s[..63];

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _exiting = true;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            StopWatchdog();
            _bootstrap.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _icon.Dispose();
            _host.Dispose();
            _startupCts?.Dispose();
            _gate.Dispose();
        }

        base.Dispose(disposing);
    }
}
