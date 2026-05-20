using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PrInbox.Core.Credentials;

/// <summary>
/// Default <see cref="IGhCliRunner"/> that shells out to the real
/// <c>gh</c> binary on PATH. Captures stdout and stderr separately and
/// reports a <see cref="GhCliResult.FailedToStart"/> flag when the
/// binary is missing — callers expect a clean signal rather than an
/// exception so they can degrade gracefully (e.g. fall back to
/// "default identity" UX when <c>gh</c> is not installed).
/// </summary>
public sealed class GhCliRunner : IGhCliRunner
{
    private readonly string _ghExecutable;
    private readonly ILogger<GhCliRunner> _logger;

    public GhCliRunner(ILogger<GhCliRunner>? logger = null, string ghExecutable = "gh")
    {
        _ghExecutable = ghExecutable;
        _logger = logger ?? NullLogger<GhCliRunner>.Instance;
    }

    public async Task<GhCliResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ghExecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        _logger.LogDebug("Invoking: {Exe} {Args}", _ghExecutable, string.Join(' ', args));

        Process? process;
        try
        {
            process = Process.Start(psi);
            if (process is null)
            {
                return new GhCliResult(-1, string.Empty, string.Empty, FailedToStart: true);
            }
        }
        catch (Win32Exception ex)
        {
            _logger.LogDebug(ex, "gh binary not found on PATH.");
            return new GhCliResult(-1, string.Empty, string.Empty, FailedToStart: true);
        }

        using (process)
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new GhCliResult(process.ExitCode, stdout, stderr, FailedToStart: false);
        }
    }
}
