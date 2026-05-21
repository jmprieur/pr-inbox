namespace PrInbox.Core.Credentials;

/// <summary>
/// One GitHub login currently authenticated via <c>gh auth status</c> on
/// a given host. Surfaced by <see cref="IGitHubAuthDiscovery"/> so the
/// Settings UI can let the user pick which login a new source is bound
/// to.
/// </summary>
/// <param name="Login">
/// The GitHub login (e.g. <c>jmprieur</c> or <c>jmprieur_microsoft</c>).
/// Stored as-is — preserving the exact casing reported by <c>gh</c>.
/// </param>
/// <param name="IsActive">
/// Whether <c>gh</c> currently marks this login as the active account
/// for the host. Informational only — the Settings UI uses it to badge
/// the picker entry. Newer multi-account <c>gh</c> versions report this
/// explicitly; older single-account versions default to <c>true</c>.
/// </param>
public sealed record GitHubAuthIdentity(string Login, bool IsActive)
{
    /// <summary>
    /// OAuth scopes granted to this login's token, as reported on the
    /// <c>- Token scopes: 'a', 'b', 'c'</c> line of <c>gh auth status</c>.
    /// Empty when the parser did not see the line (older gh versions or
    /// partial output) — callers should not treat empty as definitive
    /// "no scopes" but as "unknown".
    /// </summary>
    public IReadOnlyList<string> Scopes { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Best-effort EMU detection. GitHub Enterprise Managed User logins
    /// follow the convention <c>&lt;personal&gt;_&lt;org&gt;</c> — the
    /// underscore is the canonical signal. Pragmatic v1 heuristic: any
    /// login containing an underscore is treated as EMU for chip
    /// classification purposes. Legitimate non-EMU logins with
    /// underscores will be miscategorised here but the user can still
    /// see and override their identity in Settings.
    /// </summary>
    public bool IsEmu => Login.Contains('_');
}
