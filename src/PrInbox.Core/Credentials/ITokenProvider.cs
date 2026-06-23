namespace PrInbox.Core.Credentials;

/// <summary>
/// Acquires an OAuth/access token for a single source on demand.
/// </summary>
/// <remarks>
/// <para>
/// pr-inbox never stores tokens. Token providers shell out to the credential
/// authorities the user already uses (<c>gh</c> for GitHub.com / GHE,
/// Azure CLI for Azure DevOps).
/// </para>
/// <para>
/// Implementations should refetch on each call rather than caching, because
/// the upstream CLIs handle their own refresh semantics.
/// </para>
/// </remarks>
public interface ITokenProvider
{
    /// <summary>
    /// A short identifier for the source this provider serves
    /// (e.g. <c>gh.com</c>, <c>ghe.contoso.com</c>, <c>ado:fabrikam</c>).
    /// </summary>
    string SourceId { get; }

    /// <summary>
    /// Returns the bearer access token for this source. Throws
    /// <see cref="TokenAcquisitionException"/> on any failure with a
    /// human-actionable remediation message.
    /// </summary>
    Task<string> GetTokenAsync(CancellationToken ct = default);

    /// <summary>
    /// Best-effort identity lookup for the authenticated principal on this
    /// source: a login or display name. Used by <c>config doctor</c>.
    /// Returns null if the source cannot report identity without an API call.
    /// </summary>
    Task<string?> GetAuthenticatedIdentityAsync(CancellationToken ct = default);
}

/// <summary>
/// Thrown when a token cannot be acquired. <see cref="Exception.Message"/>
/// contains the exact CLI invocation the user can run to fix the situation.
/// </summary>
public sealed class TokenAcquisitionException : Exception
{
    public TokenAcquisitionException(string message) : base(message) { }
    public TokenAcquisitionException(string message, Exception inner) : base(message, inner) { }
}
