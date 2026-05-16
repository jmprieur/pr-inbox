namespace PrInbox.Core.Models;

/// <summary>
/// Identifies which (kind, host, identity) triple a PR was discovered by.
/// </summary>
/// <remarks>
/// <para>
/// The computed <see cref="SourceId"/> is stored on
/// <c>pull_requests.source_id</c> and as a key in the
/// <c>pr_source_bindings</c> junction table.
/// </para>
/// <para>
/// For GitHub.com and GHE, <see cref="Identity"/> is the <c>gh</c> CLI user
/// login (e.g. <c>jmprieur_microsoft</c>). For Azure DevOps, <see cref="Host"/>
/// is fixed to <c>dev.azure.com</c> and <see cref="Identity"/> is the org name
/// (e.g. <c>mseng</c>) since ADO PR discovery is org-scoped.
/// </para>
/// </remarks>
/// <param name="Kind">Source kind.</param>
/// <param name="Host">Lowercased host name.</param>
/// <param name="Identity">Identity discriminator (gh user, or ADO org).</param>
public sealed record SourceBinding(SourceKind Kind, string Host, string Identity)
{
    /// <summary>
    /// Computed source id: <c>{host}:{identity}</c>. Examples:
    /// <c>github.com:jmprieur_microsoft</c>,
    /// <c>microsoft.ghe.com:jean-marc-prieur</c>,
    /// <c>dev.azure.com:mseng</c>.
    /// </summary>
    public string SourceId => $"{Host}:{Identity}";

    public override string ToString() => SourceId;
}
