using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PrInbox.Core.Credentials;

/// <summary>
/// Acquires GitHub.com or GHE tokens by shelling out to <c>gh auth token</c>.
/// Never reads, caches, or writes the token to disk.
/// </summary>
public sealed class GhCliTokenProvider : ITokenProvider
{
    private readonly string _hostname;
    private readonly string _ghExecutable;
    private readonly ILogger<GhCliTokenProvider> _logger;

    public GhCliTokenProvider(string sourceId, string hostname, ILogger<GhCliTokenProvider>? logger = null, string ghExecutable = "gh")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        SourceId = sourceId;
        _hostname = hostname;
        _ghExecutable = ghExecutable;
        _logger = logger ?? NullLogger<GhCliTokenProvider>.Instance;
    }

    public string SourceId { get; }

    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        var (exitCode, stdout, stderr) = await RunAsync(
            new[] { "auth", "token", "--hostname", _hostname }, ct);

        if (exitCode != 0)
        {
            throw new TokenAcquisitionException(
                $"gh auth token failed for host '{_hostname}'. " +
                $"Run: gh auth login --hostname {_hostname}\n" +
                $"  exit code: {exitCode}\n" +
                $"  stderr: {stderr.Trim()}");
        }

        var token = stdout.Trim();
        if (string.IsNullOrEmpty(token))
        {
            throw new TokenAcquisitionException(
                $"gh auth token returned an empty token for host '{_hostname}'. " +
                $"Run: gh auth login --hostname {_hostname}");
        }
        return token;
    }

    public async Task<string?> GetAuthenticatedIdentityAsync(CancellationToken ct = default)
    {
        // gh api user returns JSON; we just want the login field.
        var (exitCode, stdout, _) = await RunAsync(
            new[] { "api", "user", "--hostname", _hostname, "-q", ".login" }, ct);

        if (exitCode != 0)
        {
            return null;
        }
        var login = stdout.Trim();
        return string.IsNullOrEmpty(login) ? null : login;
    }

    private async Task<(int exitCode, string stdout, string stderr)> RunAsync(
        IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ghExecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        var argsForLog = string.Join(' ', args);
        _logger.LogDebug("Invoking: {Exe} {Args}", _ghExecutable, argsForLog);

        try
        {
            using var process = Process.Start(psi)
                ?? throw new TokenAcquisitionException(
                    $"Failed to start '{_ghExecutable}'. Install from https://cli.github.com/.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return (process.ExitCode, stdout, stderr);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new TokenAcquisitionException(
                $"'{_ghExecutable}' not found on PATH. Install from https://cli.github.com/ then run 'gh auth login --hostname {_hostname}'.",
                ex);
        }
    }
}
