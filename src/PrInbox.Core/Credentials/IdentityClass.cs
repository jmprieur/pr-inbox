namespace PrInbox.Core.Credentials;

/// <summary>
/// One rule in the configurable identity taxonomy. A login is classified by
/// the first rule whose <see cref="Host"/> matches and whose login ends with
/// <see cref="AliasSuffix"/>. Examples (the Microsoft profile):
/// <list type="bullet">
///   <item><c>EMU</c> — host <c>github.com</c>, suffix <c>_microsoft</c></item>
///   <item><c>Proxima</c> — host <c>microsoft.ghe.com</c>, suffix <c>_microsoft</c></item>
///   <item><c>Public</c> — host <c>github.com</c>, no suffix (fallback)</item>
/// </list>
/// The default shipped taxonomy is generic (just <c>Public</c> on github.com);
/// Microsoft-specific classes come from an imported profile, so nothing about
/// any one enterprise is hardcoded.
/// </summary>
public sealed class IdentityClass
{
    /// <summary>Class name / chip label, e.g. <c>EMU</c>, <c>Proxima</c>, <c>Public</c>.</summary>
    public string Name { get; init; } = "";

    /// <summary>Host this class applies to, e.g. <c>github.com</c>. Empty matches any host.</summary>
    public string Host { get; init; } = "";

    /// <summary>Login suffix marking membership, e.g. <c>_microsoft</c>. Empty matches any login on the host.</summary>
    public string AliasSuffix { get; init; } = "";
}

/// <summary>
/// Classifies logins against a configurable list of <see cref="IdentityClass"/>
/// rules. Generic over the class name (no enterprise is hardcoded), so the same
/// machinery serves EMU, Proxima, Public, or any org-defined class.
/// </summary>
public static class IdentityClassifier
{
    /// <summary>
    /// Returns the <see cref="IdentityClass.Name"/> of the first rule matching
    /// (<paramref name="host"/>, <paramref name="login"/>), or <c>null</c>.
    /// </summary>
    public static string? Classify(string? login, string? host, IReadOnlyList<IdentityClass>? classes)
    {
        if (classes is null) return null;
        foreach (var c in classes)
        {
            if (!HostMatches(host, c.Host)) continue;
            if (!SuffixMatches(login, c.AliasSuffix)) continue;
            return c.Name;
        }
        return null;
    }

    /// <summary>
    /// True when (<paramref name="host"/>, <paramref name="login"/>) is classified
    /// as <paramref name="code"/> (e.g. <c>"EMU"</c>).
    /// </summary>
    public static bool IsShortCode(string code, string? login, string? host, IReadOnlyList<IdentityClass>? classes)
        => string.Equals(Classify(login, host, classes), code, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when (<paramref name="host"/>, <paramref name="login"/>) matches a
    /// rule with a non-empty <see cref="IdentityClass.AliasSuffix"/> — i.e. a
    /// "managed" identity (EMU/Proxima) rather than a bare/public one. Used to
    /// prefer the managed identity as a host default.
    /// </summary>
    public static bool IsManaged(string? login, string? host, IReadOnlyList<IdentityClass>? classes)
    {
        if (classes is null) return false;
        foreach (var c in classes)
        {
            if (string.IsNullOrEmpty(c.AliasSuffix)) continue;
            if (HostMatches(host, c.Host) && SuffixMatches(login, c.AliasSuffix)) return true;
        }
        return false;
    }

    /// <summary>
    /// Strips a matching rule's <see cref="IdentityClass.AliasSuffix"/> from
    /// <paramref name="login"/> for display (e.g. <c>jmprieur_microsoft → jmprieur</c>).
    /// When <paramref name="host"/> is <c>null</c> the host check is skipped (suffix-only),
    /// which suits display contexts that don't carry a host. A login that is nothing
    /// but the suffix is returned unchanged.
    /// </summary>
    public static string StripAliasSuffix(string? login, string? host, IReadOnlyList<IdentityClass>? classes)
    {
        if (string.IsNullOrEmpty(login) || classes is null) return login ?? string.Empty;
        foreach (var c in classes)
        {
            if (string.IsNullOrEmpty(c.AliasSuffix)) continue;
            if (host is not null && !HostMatches(host, c.Host)) continue;
            if (login.Length > c.AliasSuffix.Length &&
                login.EndsWith(c.AliasSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return login[..^c.AliasSuffix.Length];
            }
        }
        return login;
    }

    private static bool HostMatches(string? host, string? ruleHost)
        => string.IsNullOrEmpty(ruleHost) ||
           string.Equals(host, ruleHost, StringComparison.OrdinalIgnoreCase);

    private static bool SuffixMatches(string? login, string? suffix)
        => string.IsNullOrEmpty(suffix) ||
           (!string.IsNullOrEmpty(login) && login.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
}
