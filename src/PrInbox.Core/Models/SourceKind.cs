namespace PrInbox.Core.Models;

/// <summary>
/// Identifies the kind of source platform a PR came from.
/// </summary>
public enum SourceKind
{
    GitHub,
    GitHubEnterprise,
    AzureDevOps,
}

/// <summary>
/// Helpers for mapping <see cref="SourceKind"/> to/from the canonical
/// string values stored in <c>pull_requests.source_kind</c>.
/// </summary>
public static class SourceKindExtensions
{
    public static string ToDbValue(this SourceKind kind) => kind switch
    {
        SourceKind.GitHub => "github",
        SourceKind.GitHubEnterprise => "github-enterprise",
        SourceKind.AzureDevOps => "azure-devops",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static SourceKind FromDbValue(string value) => value switch
    {
        "github" => SourceKind.GitHub,
        "github-enterprise" => SourceKind.GitHubEnterprise,
        "azure-devops" => SourceKind.AzureDevOps,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown SourceKind"),
    };
}
