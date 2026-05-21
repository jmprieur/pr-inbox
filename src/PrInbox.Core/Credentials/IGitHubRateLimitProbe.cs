namespace PrInbox.Core.Credentials;

/// <summary>
/// Probes the GitHub REST API <c>/rate_limit</c> endpoint via <c>gh api</c>
/// for a given host. Used by the Doctor's rate-limit-headroom advisory
/// to surface "you're about to get throttled" warnings before users
/// notice empty inboxes.
/// </summary>
public interface IGitHubRateLimitProbe
{
    /// <summary>
    /// Returns the current <c>core</c> rate-limit snapshot for
    /// <paramref name="hostname"/>, or <c>null</c> if the probe failed
    /// (gh missing, not logged in, network down, parse failed). Never
    /// throws — Doctor stays usable even when this leg fails.
    /// </summary>
    Task<RateLimitSnapshot?> GetCoreAsync(string hostname, CancellationToken ct = default);
}

/// <param name="Remaining">Requests left in the current window.</param>
/// <param name="Limit">Total requests permitted per window.</param>
/// <param name="ResetAt">UTC time when the window resets.</param>
public sealed record RateLimitSnapshot(int Remaining, int Limit, DateTimeOffset ResetAt)
{
    /// <summary>Fraction of the budget remaining, in [0,1].</summary>
    public double RemainingFraction => Limit <= 0 ? 1.0 : (double)Remaining / Limit;
}
