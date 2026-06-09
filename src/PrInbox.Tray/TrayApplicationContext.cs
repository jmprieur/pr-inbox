using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

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

    private WebHostProcess _host;
    private CancellationTokenSource? _startupCts;
    private SynchronizationContext? _ui;
    private volatile bool _exiting;
    private bool _running;

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

    private async Task RestartAsync()
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

                _openItem.Enabled = false;
                _restartItem.Enabled = false;
                _running = false;
                _notifyIcon.Text = "PR Inbox — restarting…";

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
            // Only surface this if the server had actually come up; a child that
            // dies during the initial start sequence is reported by StartCore.
            if (_exiting || !_running)
            {
                return;
            }

            _running = false;
            _openItem.Enabled = false;
            _restartItem.Enabled = true;
            _notifyIcon.Text = "PR Inbox — stopped";
            ShowBalloon(
                "PR Inbox stopped",
                "The web server exited. Use 'View log' to see why, or Restart.",
                ToolTipIcon.Warning);
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
