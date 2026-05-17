using PrInbox.Core.Credentials;

namespace PrInbox.Core.Config;

/// <summary>
/// Structured result of <see cref="IConfigService.RunDoctorAsync"/>. The
/// Web UI binds directly to this; the CLI doctor still uses Spectre
/// markup independently.
/// </summary>
public sealed record DoctorReport(
    IReadOnlyList<SourceCheck> Sources,
    IReadOnlyList<AdoProjectInfo> AdoProjects,
    bool AllOk,
    string ConfigPath);

/// <summary>
/// One row in the doctor's per-source table.
/// </summary>
/// <param name="Id">Source id (e.g. <c>gh.com</c>).</param>
/// <param name="Kind">Source kind.</param>
/// <param name="Host">Host for GitHub-flavored sources; null for ADO.</param>
/// <param name="Enabled">Whether the source is enabled in config.</param>
/// <param name="Ok">True if a token + identity were acquired successfully.</param>
/// <param name="Identity">Resolved authenticated identity (login) when <paramref name="Ok"/>.</param>
/// <param name="TokenLength">Length of the acquired token, when <paramref name="Ok"/>. Useful as a smoke signal without exposing the token.</param>
/// <param name="Error">First line of the underlying error message when <paramref name="Ok"/> is false.</param>
public sealed record SourceCheck(
    string Id,
    SourceConfigKind Kind,
    string? Host,
    bool Enabled,
    bool Ok,
    string? Identity,
    int? TokenLength,
    string? Error);

/// <summary>Display row for an ADO (org, project) entry.</summary>
public sealed record AdoProjectInfo(string Org, string Project);
