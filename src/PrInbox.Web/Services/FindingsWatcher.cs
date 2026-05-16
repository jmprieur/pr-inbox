using PrInbox.Core.Findings;

namespace PrInbox.Web.Services;

/// <summary>
/// Watches a review run's directory for <c>findings.yaml</c> and
/// surfaces the parsed document to <see cref="ReviewRunStore"/>.
/// One watcher per active run; disposed when the run is replaced or
/// the web app shuts down.
/// </summary>
public sealed class FindingsWatcher : IDisposable
{
    private readonly FileSystemWatcher _fsw;
    private readonly ReviewRunStore _store;
    private readonly string _prUrl;
    private readonly string _runDir;
    private readonly ILogger _log;
    private readonly FindingsParser _parser = new();
    private readonly System.Threading.Timer _debounce;
    private int _disposed;

    public FindingsWatcher(string prUrl, string runDir, ReviewRunStore store, ILogger log)
    {
        _prUrl = prUrl;
        _runDir = runDir;
        _store = store;
        _log = log;

        Directory.CreateDirectory(runDir);

        _fsw = new FileSystemWatcher(runDir, "findings.yaml")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _fsw.Created += (_, _) => Trigger();
        _fsw.Changed += (_, _) => Trigger();
        _fsw.Renamed += (_, _) => Trigger();

        _debounce = new System.Threading.Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);

        // Initial read in case the file already exists (e.g., a previous run).
        if (File.Exists(Path.Combine(runDir, "findings.yaml")))
        {
            Trigger();
        }
    }

    private void Trigger()
    {
        // FileSystemWatcher fires multiple events for one save; debounce.
        try { _debounce.Change(250, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    private void Reload()
    {
        if (_disposed != 0) return;
        var path = Path.Combine(_runDir, "findings.yaml");
        if (!File.Exists(path)) return;

        try
        {
            string text;
            // Tolerate the writer still holding the file briefly.
            for (var i = 0; ; i++)
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs);
                    text = reader.ReadToEnd();
                    break;
                }
                catch (IOException) when (i < 5)
                {
                    Thread.Sleep(100);
                }
            }

            var result = _parser.ParseLenient(text);
            _store.UpdateFindings(_prUrl, result.Document, result.Errors);
            _log.LogInformation("findings.yaml for {Url} reloaded: {Findings} finding(s), {Errors} schema issue(s).",
                _prUrl, result.Document?.Findings.Count ?? 0, result.Errors.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read findings.yaml at {Path}", path);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _fsw.EnableRaisingEvents = false; } catch { }
        _fsw.Dispose();
        _debounce.Dispose();
    }
}
