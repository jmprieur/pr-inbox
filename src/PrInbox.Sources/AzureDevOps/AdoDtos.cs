using System.Text.Json.Serialization;

namespace PrInbox.Sources.AzureDevOps;

/// <summary>
/// JSON DTOs for Azure DevOps REST responses. Field names follow the wire
/// format (PascalCase via JSON converter); explicit <see cref="JsonPropertyName"/>
/// attributes are used for fields whose names diverge from the C# property name.
/// </summary>
internal static class AdoDtos
{
    /// <summary>Wrapper for ADO list responses: <c>{ count, value: [...] }</c>.</summary>
    public sealed class ListResponse<T>
    {
        [JsonPropertyName("count")] public int Count { get; init; }
        [JsonPropertyName("value")] public List<T> Value { get; init; } = new();
    }

    /// <summary>Profile response from <c>vssps.dev.azure.com/_apis/profile/profiles/me</c>.</summary>
    public sealed class ProfileResponse
    {
        [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
        [JsonPropertyName("displayName")] public string? DisplayName { get; init; }
        [JsonPropertyName("emailAddress")] public string? EmailAddress { get; init; }
        [JsonPropertyName("publicAlias")] public string? PublicAlias { get; init; }
    }

    /// <summary>
    /// Subset of the ADO Pull Request entity used by the adapter.
    /// Only fields the adapter actually reads are modeled.
    /// </summary>
    public sealed class PullRequest
    {
        [JsonPropertyName("pullRequestId")] public int PullRequestId { get; init; }
        [JsonPropertyName("title")] public string? Title { get; init; }
        [JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("creationDate")] public DateTimeOffset CreationDate { get; init; }
        [JsonPropertyName("lastMergeSourceCommit")] public Commit? LastMergeSourceCommit { get; init; }
        [JsonPropertyName("lastMergeTargetCommit")] public Commit? LastMergeTargetCommit { get; init; }
        [JsonPropertyName("lastMergeCommit")] public Commit? LastMergeCommit { get; init; }
        [JsonPropertyName("createdBy")] public Identity? CreatedBy { get; init; }
        [JsonPropertyName("reviewers")] public List<Reviewer> Reviewers { get; init; } = new();
        [JsonPropertyName("repository")] public Repository? Repository { get; init; }
        [JsonPropertyName("isDraft")] public bool IsDraft { get; init; }
        [JsonPropertyName("sourceRefName")] public string? SourceRefName { get; init; }
        [JsonPropertyName("targetRefName")] public string? TargetRefName { get; init; }
    }

    public sealed class Commit
    {
        [JsonPropertyName("commitId")] public string CommitId { get; init; } = string.Empty;
        [JsonPropertyName("author")] public CommitSignature? Author { get; init; }
        [JsonPropertyName("comment")] public string? Comment { get; init; }
    }

    public sealed class CommitSignature
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("email")] public string? Email { get; init; }
        [JsonPropertyName("date")] public DateTimeOffset Date { get; init; }
    }

    public sealed class Identity
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("displayName")] public string? DisplayName { get; init; }
        [JsonPropertyName("uniqueName")] public string? UniqueName { get; init; }

        /// <summary>True if this identity represents a service (bot) account.</summary>
        [JsonPropertyName("isContainer")] public bool IsContainer { get; init; }
    }

    public sealed class Reviewer
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("displayName")] public string? DisplayName { get; init; }
        [JsonPropertyName("uniqueName")] public string? UniqueName { get; init; }

        /// <summary>0=no vote, 10=approved, 5=approved with suggestions, -5=waiting, -10=rejected.</summary>
        [JsonPropertyName("vote")] public int Vote { get; init; }

        [JsonPropertyName("isRequired")] public bool IsRequired { get; init; }
    }

    public sealed class Repository
    {
        [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
        [JsonPropertyName("project")] public Project? Project { get; init; }
    }

    public sealed class Project
    {
        [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    }

    /// <summary>One PR conversation thread.</summary>
    public sealed class Thread
    {
        [JsonPropertyName("id")] public int Id { get; init; }
        [JsonPropertyName("comments")] public List<Comment> Comments { get; init; } = new();
        [JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("threadContext")] public ThreadContext? ThreadContext { get; init; }
        [JsonPropertyName("publishedDate")] public DateTimeOffset? PublishedDate { get; init; }
        [JsonPropertyName("lastUpdatedDate")] public DateTimeOffset? LastUpdatedDate { get; init; }
        [JsonPropertyName("isDeleted")] public bool IsDeleted { get; init; }
    }

    public sealed class ThreadContext
    {
        [JsonPropertyName("filePath")] public string? FilePath { get; init; }
    }

    public sealed class Comment
    {
        [JsonPropertyName("id")] public int Id { get; init; }
        [JsonPropertyName("author")] public Identity? Author { get; init; }
        [JsonPropertyName("content")] public string? Content { get; init; }
        [JsonPropertyName("publishedDate")] public DateTimeOffset PublishedDate { get; init; }
        [JsonPropertyName("lastUpdatedDate")] public DateTimeOffset? LastUpdatedDate { get; init; }
        [JsonPropertyName("commentType")] public string? CommentType { get; init; }
        [JsonPropertyName("isDeleted")] public bool IsDeleted { get; init; }
    }
}
