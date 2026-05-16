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
    private readonly string? _identity;
    private readonly string _ghExecutable;
    private readonly ILogger<GhCliTokenProvider> _logger;

    public GhCliTokenProvider(
        string sourceId,
        string hostname,
        string? identity = null,
        ILogger<GhCliTokenProvider>? logger = null,
        string ghExecutable = "gh")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        SourceId = sourceId;
        _hostname = hostname;
        // Treat null, empty, whitespace, or the placeholder "default" as
        // "no specific identity" — the gh CLI then picks the default-active
        // account for that host. Anything else is passed as `--user <identity>`.
        _identity = string.IsNullOrWhiteSpace(identity) ||
                    string.Equals(identity, "default", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : identity;
        _ghExecutable = ghExecutable;
        _logger = logger ?? NullLogger<GhCliTokenProvider>.Instance;
    }

    public string SourceId { get; }

    /// <summary>
    /// The identity (gh CLI user) this provider is bound to, if any. Null
    /// means "use the default-active account for the host".
    /// </summary>
    public string? Identity => _identity;

    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        var args = new List<string> { "auth", "token", "--hostname", _hostname };
        if (_identity is not null)
        {
            args.Add("--user");
            args.Add(_identity);
        }

        var (exitCode, stdout, stderr) = await RunAsync(args, ct);

        if (exitCode != 0)
        {
            var userHint = _identity is null ? string.Empty : $" --user {_identity}";
            throw new TokenAcquisitionException(
                $"gh auth token failed for host '{_hostname}'{(_identity is null ? string.Empty : $" / user '{_identity}'")}. " +
                $"Run: gh auth login --hostname {_hostname}{userHint}\n" +
                $"  exit code: {exitCode}\n" +
                $"  stderr: {stderr.Trim()}");
        }

        var token = stdout.Trim();
        if (string.IsNullOrEmpty(token))
        {
            throw new TokenAcquisitionException(
                $"gh auth token returned an empty token for host '{_hostname}'" +
                (_identity is null ? "." : $" / user '{_identity}'.") +
                $" Run: gh auth login --hostname {_hostname}" +
                (_identity is null ? string.Empty : $" --user {_identity}"));
        }
        return token;
    }

    public async Task<string?> GetAuthenticatedIdentityAsync(CancellationToken ct = default)
    {
        // If an explicit identity is configured, return it without a probe.
        // (`gh api` does not accept --user, so we can't ask it which login a
        // given user-scoped token belongs to. The configured identity *is*
        // the answer.)
        if (_identity is not null)
        {
            return _identity;
        }

        // For "default" / unspecified identity, probe via gh api user.
        var (exitCode, stdout, _) = await RunAsync(
            new List<string> { "api", "user", "--hostname", _hostname, "-q", ".login" }, ct);

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
