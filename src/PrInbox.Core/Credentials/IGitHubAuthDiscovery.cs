namespace PrInbox.Core.Credentials;

/// <summary>
/// Discovers the GitHub logins authenticated via <c>gh</c> on a given
/// host. Used by the Settings UI to offer an identity picker when the
/// user adds a new GitHub source. Implementations shell out to
/// <c>gh auth status</c>; failures (missing <c>gh</c>, no logins,
/// timeout) all collapse to an empty list so the caller can fall back
/// to legacy "default identity" behaviour.
/// </summary>
public interface IGitHubAuthDiscovery
{
    /// <summary>
    /// Returns the logins currently authenticated on <paramref name="hostname"/>.
    /// Empty list = nothing found / <c>gh</c> not installed / probe
    /// failed. Never throws; failures are swallowed and logged.
    /// </summary>
    Task<IReadOnlyList<GitHubAuthIdentity>> ListIdentitiesAsync(
        string hostname,
        CancellationToken ct = default);
}
