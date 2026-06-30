using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace PrInbox.Tray;

/// <summary>
/// Owns the hidden pr-inbox-web.exe child: resolves its path, launches it with
/// a deterministic loopback URL and a one-time shutdown token, drains its
/// stdout/stderr to a log file, supervises it via a Job Object, and stops it
/// gracefully (POST /shutdown) with a hard-kill fallback.
/// </summary>
internal sealed class WebHostProcess : IDisposable
{
    private const string PreferredPortVar = "PRINBOX_TRAY_PORT";
    private const string WebExeOverrideVar = "PRINBOX_WEB_EXE";
    private const int PreferredPort = 7341;

    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private readonly object _logLock = new();

    private Process? _process;
    private NativeJob? _job;
    private StreamWriter? _logWriter;
    private volatile bool _stopRequested;

    /// <summary>Loopback URL the web app listens on, e.g. http://localhost:7341.</summary>
    public string BaseUrl { get; }

    /// <summary>Resolved path of the web exe (may not exist yet — checked at Start).</summary>
    public string WebExePath { get; }

    /// <summary>File the child's console output is mirrored to.</summary>
    public string LogFilePath { get; }

    /// <summary>Raised (on a thread-pool thread) when the child exits without a
    /// Stop being requested.</summary>
    public event Action? ExitedUnexpectedly;

    public WebHostProcess()
    {
        var port = PickPort();
        BaseUrl = $"http://localhost:{port}";
        WebExePath = ResolveWebExe();

        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PrInbox", "logs");
        Directory.CreateDirectory(logDir);
        LogFilePath = Path.Combine(logDir, $"web-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    }

    /// <summary>Launches the hidden web child. Throws if the exe is missing.</summary>
    public void Start()
    {
        TrayLog.Write($"Start: resolved web exe = {WebExePath}");
        TrayLog.Write($"Start: base url = {BaseUrl}");

        if (!File.Exists(WebExePath))
        {
            throw new FileNotFoundException(
                $"Could not find the web server executable.\nExpected: {WebExePath}\n\n" +
                "Build the solution (dotnet build PrInbox.slnx) so pr-inbox-web.exe exists.",
                WebExePath);
        }

        _logWriter = new StreamWriter(LogFilePath, append: true) { AutoFlush = true };

        var psi = new ProcessStartInfo
        {
            FileName = WebExePath,
            WorkingDirectory = Path.GetDirectoryName(WebExePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["ASPNETCORE_URLS"] = BaseUrl;
        psi.Environment["PRINBOX_SHUTDOWN_TOKEN"] = _token;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => WriteLog(e.Data);
        process.ErrorDataReceived += (_, e) => WriteLog(e.Data);
        process.Exited += OnProcessExited;

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
        TrayLog.Write($"Start: child pid = {process.Id}");

        // Supervise: kill the child if the tray dies abnormally.
        _job = NativeJob.Create();
        var assigned = _job?.Assign(process) ?? false;
        TrayLog.Write($"Start: job created = {_job is not null}, assigned = {assigned}");
    }

    /// <summary>Polls /healthz until 200 or the timeout elapses. Returns false
    /// if the child exits first or never becomes healthy.</summary>
    public async Task<bool> WaitForHealthyAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested || _process is { HasExited: true })
            {
                return false;
            }

            try
            {
                using var resp = await http.GetAsync($"{BaseUrl}/healthz", ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
            catch
            {
                // Not listening yet (or per-request http timeout) — keep polling.
            }

            try
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>One-shot liveness check: true only if the child process is still
    /// alive AND /healthz answers 200 within a short timeout. Used by the tray's
    /// watchdog to notice a web that's gone or wedged while the process (or just
    /// the tray) lingers — e.g. after the machine sleeps and wakes.</summary>
    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        if (_process is null || _process.HasExited)
        {
            return false;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var resp = await http.GetAsync($"{BaseUrl}/healthz", ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Graceful stop: POST /shutdown, wait for exit, hard-kill on
    /// timeout. Idempotent.</summary>
    public async Task StopAsync()
    {
        _stopRequested = true;
        var process = _process;
        if (process is null)
        {
            DisposeJob();
            return;
        }

        if (!process.HasExited)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/shutdown");
                req.Headers.TryAddWithoutValidation("X-Shutdown-Token", _token);
                using var resp = await http.SendAsync(req).ConfigureAwait(false);
            }
            catch
            {
                // Fall through to wait + kill.
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Graceful path timed out.
            }

            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch { /* already gone */ }
            }
        }

        FlushChildOutput();
        DisposeJob();
    }

    public void Dispose()
    {
        try
        {
            if (!_stopRequested)
            {
                StopAsync().GetAwaiter().GetResult();
            }
        }
        catch
        {
            // Best-effort during teardown.
        }

        FlushChildOutput();
        DisposeJob();
        _process?.Dispose();

        lock (_logLock)
        {
            _logWriter?.Dispose();
            _logWriter = null;
        }
    }

    // After the child has exited, the no-arg WaitForExit() guarantees the
    // async OutputDataReceived/ErrorDataReceived handlers have drained, so the
    // log captures the child's final lines before the writer is disposed.
    private void FlushChildOutput()
    {
        try
        {
            if (_process is { HasExited: true })
            {
                _process.WaitForExit();
            }
        }
        catch
        {
            // Best-effort.
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var code = -1;
        try { code = _process?.ExitCode ?? -1; } catch { }
        TrayLog.Write($"Child exited: code = {code}, stopRequested = {_stopRequested}");

        if (!_stopRequested)
        {
            ExitedUnexpectedly?.Invoke();
        }
    }

    private void WriteLog(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_logLock)
        {
            _logWriter?.WriteLine(line);
        }
    }

    private void DisposeJob()
    {
        _job?.Dispose();
        _job = null;
    }

    private static int PickPort()
    {
        var preferred = PreferredPort;
        var configured = Environment.GetEnvironmentVariable(PreferredPortVar);
        if (int.TryParse(configured, out var parsed) && parsed is > 0 and < 65536)
        {
            preferred = parsed;
        }

        if (IsPortFree(preferred))
        {
            return preferred;
        }

        // Preferred port busy (e.g. a dev instance already running) — grab a
        // free ephemeral port. The tray owns the browser-open URL, so the
        // exact number doesn't need to be memorable.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveWebExe()
    {
        const string exeName = "pr-inbox-web.exe";
        const string dllName = "pr-inbox-web.dll";

        // The apphost (.exe) is just a shim that loads the matching managed
        // .dll from its own folder; a lone .exe (which can leak into the tray's
        // own output dir via the build dependency) is NOT runnable. Require the
        // companion .dll so we never select a dead shim.
        static bool Runnable(string exePath) =>
            File.Exists(exePath) &&
            File.Exists(Path.Combine(Path.GetDirectoryName(exePath)!, dllName));

        // 1. Explicit override (published / custom layouts).
        var overridePath = Environment.GetEnvironmentVariable(WebExeOverrideVar);
        if (!string.IsNullOrWhiteSpace(overridePath) && Runnable(overridePath))
        {
            return overridePath;
        }

        var baseDir = AppContext.BaseDirectory;

        // 2. Side-by-side (published single-folder layout: tray + web together).
        var sideBySide = Path.Combine(baseDir, exeName);
        if (Runnable(sideBySide))
        {
            return sideBySide;
        }

        // 3. Dev layout: derive the sibling Web bin folder for the same config.
        //    baseDir = ...\src\PrInbox.Tray\bin\<Config>\net10.0-windows\
        try
        {
            var tfmDir = new DirectoryInfo(baseDir.TrimEnd(Path.DirectorySeparatorChar));
            var configDir = tfmDir.Parent;                  // <Config>
            var srcDir = configDir?.Parent?.Parent?.Parent; // bin -> PrInbox.Tray -> src
            if (configDir is not null && srcDir is not null)
            {
                // Returned whether or not it's runnable, so a missing build
                // yields a precise "expected here" message from Start().
                return Path.Combine(
                    srcDir.FullName, "PrInbox.Web", "bin", configDir.Name, "net10.0", exeName);
            }
        }
        catch
        {
            // Fall through to the side-by-side guess.
        }

        return sideBySide;
    }
}
