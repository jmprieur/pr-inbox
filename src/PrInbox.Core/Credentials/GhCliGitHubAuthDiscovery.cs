using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PrInbox.Core.Credentials;

/// <summary>
/// Discovers GitHub logins by invoking <c>gh auth status --hostname &lt;host&gt;</c>
/// and parsing the text via <see cref="GhAuthStatusParser"/>. Designed
/// to never throw: every failure mode (gh not installed, gh not logged
/// in, parse-empty, timeout) collapses to an empty result so the caller
/// can degrade to "default identity" UX.
/// </summary>
public sealed class GhCliGitHubAuthDiscovery : IGitHubAuthDiscovery
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    private readonly IGhCliRunner _runner;
    private readonly ILogger<GhCliGitHubAuthDiscovery> _logger;

    public GhCliGitHubAuthDiscovery(IGhCliRunner runner, ILogger<GhCliGitHubAuthDiscovery>? logger = null)
    {
        _runner = runner;
        _logger = logger ?? NullLogger<GhCliGitHubAuthDiscovery>.Instance;
    }

    public async Task<IReadOnlyList<GitHubAuthIdentity>> ListIdentitiesAsync(
        string hostname,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(hostname)) return Array.Empty<GitHubAuthIdentity>();
        var host = hostname.Trim();

        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(ProbeTimeout);

        GhCliResult result;
        try
        {
            result = await _runner.RunAsync(new[] { "auth", "status", "--hostname", host }, probeCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("gh auth status timed out after {Timeout} for host {Host}.", ProbeTimeout, host);
            return Array.Empty<GitHubAuthIdentity>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "gh auth status threw for host {Host}; treating as no identities.", host);
            return Array.Empty<GitHubAuthIdentity>();
        }

        if (result.FailedToStart)
        {
            _logger.LogInformation("gh CLI not available; cannot discover identities for {Host}.", host);
            return Array.Empty<GitHubAuthIdentity>();
        }

        // gh writes status to stderr in many versions; combine both
        // streams before parsing so we don't care which is which.
        var combined = string.Concat(result.StdOut, "\n", result.StdErr);
        return GhAuthStatusParser.Parse(combined, host);
    }
}
