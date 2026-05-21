using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PrInbox.Core.Credentials;

/// <summary>
/// Default <see cref="IGitHubRateLimitProbe"/> implementation. Shells
/// out to <c>gh api --hostname &lt;host&gt; /rate_limit</c> and parses
/// the <c>resources.core</c> block. Every failure mode collapses to
/// <c>null</c> so the Doctor caller can degrade silently.
/// </summary>
public sealed class GhCliRateLimitProbe : IGitHubRateLimitProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    private readonly IGhCliRunner _runner;
    private readonly ILogger<GhCliRateLimitProbe> _logger;

    public GhCliRateLimitProbe(IGhCliRunner runner, ILogger<GhCliRateLimitProbe>? logger = null)
    {
        _runner = runner;
        _logger = logger ?? NullLogger<GhCliRateLimitProbe>.Instance;
    }

    public async Task<RateLimitSnapshot?> GetCoreAsync(string hostname, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(hostname)) return null;
        var host = hostname.Trim();

        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(ProbeTimeout);

        GhCliResult result;
        try
        {
            result = await _runner.RunAsync(
                new[] { "api", "--hostname", host, "/rate_limit" },
                probeCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("gh api /rate_limit timed out after {Timeout} for host {Host}.", ProbeTimeout, host);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "gh api /rate_limit threw for host {Host}; treating as unknown.", host);
            return null;
        }

        if (result.FailedToStart || result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            if (!doc.RootElement.TryGetProperty("resources", out var resources)) return null;
            if (!resources.TryGetProperty("core", out var core)) return null;

            var remaining = core.GetProperty("remaining").GetInt32();
            var limit = core.GetProperty("limit").GetInt32();
            var resetEpoch = core.GetProperty("reset").GetInt64();
            var resetAt = DateTimeOffset.FromUnixTimeSeconds(resetEpoch);
            return new RateLimitSnapshot(remaining, limit, resetAt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse gh api /rate_limit JSON for host {Host}.", host);
            return null;
        }
    }
}
