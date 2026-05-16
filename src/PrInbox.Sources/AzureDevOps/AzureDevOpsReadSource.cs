using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PrInbox.Core.Credentials;
using PrInbox.Core.Models;
using PrInbox.Sources.GitHub;

namespace PrInbox.Sources.AzureDevOps;

/// <summary>
/// Read-only source adapter for Azure DevOps. One instance per configured
/// (org, project) pair — ADO has no global cross-project reviewer inbox, so
/// the adapter is scoped per project.
/// </summary>
public sealed class AzureDevOpsReadSource : IPrReadSource
{
    private readonly string _org;
    private readonly string _project;
    private readonly AdoApiClient _client;
    private readonly BotDetector _botDetector;
    private readonly ILogger<AzureDevOpsReadSource> _logger;

    /// <summary>Cached VSTS profile id; resolved once per process.</summary>
    private string? _cachedReviewerId;

    public AzureDevOpsReadSource(
        string sourceId,
        string org,
        string project,
        ITokenProvider tokenProvider,
        BotDetector? botDetector = null,
        HttpClient? http = null,
        ILogger<AzureDevOpsReadSource>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(org);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);

        SourceId = sourceId;
        _org = org;
        _project = project;
        _client = new AdoApiClient(org, tokenProvider, http);
        _botDetector = botDetector ?? new BotDetector();
        _logger = logger ?? NullLogger<AzureDevOpsReadSource>.Instance;
    }

    /// <summary>Internal constructor for tests: inject a pre-built API client.</summary>
    internal AzureDevOpsReadSource(
        string sourceId,
        string org,
        string project,
        AdoApiClient client,
        BotDetector? botDetector = null,
        ILogger<AzureDevOpsReadSource>? logger = null)
    {
        SourceId = sourceId;
        _org = org;
        _project = project;
        _client = client;
        _botDetector = botDetector ?? new BotDetector();
        _logger = logger ?? NullLogger<AzureDevOpsReadSource>.Instance;
    }

    public string SourceId { get; }

    public SourceKind Kind => SourceKind.AzureDevOps;

    public SourceCapabilities Capabilities { get; } = new(
        SupportsGlobalReviewerInbox: false,
        SupportsThreadResolution: true,
        SupportsBotAuthorClassification: true,
        SupportsReviewRequestTimestamps: false,
        SupportsStableRepoIds: true,
        SupportsForcePushDetection: false);

    public async IAsyncEnumerable<RemotePullRequest> ListAssignedFastAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var reviewerId = await ResolveReviewerIdAsync(ct);

        await foreach (var pr in _client.ListPullRequestsForReviewerAsync(_project, reviewerId, pageSize: 100, ct))
        {
            ct.ThrowIfCancellationRequested();
            var mapped = MapListItem(pr);
            if (mapped is not null)
            {
                yield return mapped;
            }
        }
    }

    public async Task<PrEnrichmentBundle> EnrichAsync(PrIdentity id, CancellationToken ct)
    {
        var (repoId, prId) = ParseAdoUrl(id.Url);

        var detailTask = _client.GetPullRequestAsync(_project, repoId, prId, ct);
        var threadsTask = _client.GetThreadsAsync(_project, repoId, prId, ct);
        var commitsTask = _client.GetCommitsAsync(_project, repoId, prId, ct);

        await Task.WhenAll(detailTask, threadsTask, commitsTask);

        var detail = MapDetail(id, detailTask.Result, commitsTask.Result);
        var threads = MapThreads(threadsTask.Result);
        return new PrEnrichmentBundle(detail, threads);
    }

    public async Task<IReadOnlyList<RemoteCommit>> GetCommitsAsync(PrIdentity id, CancellationToken ct)
    {
        var (repoId, prId) = ParseAdoUrl(id.Url);
        var commits = await _client.GetCommitsAsync(_project, repoId, prId, ct);
        return commits
            .Select(c => new RemoteCommit(
                Sha: c.CommitId,
                AuthorLogin: c.Author?.Email ?? c.Author?.Name,
                CommittedAt: c.Author?.Date ?? DateTimeOffset.UtcNow,
                Subject: FirstLineOf(c.Comment ?? string.Empty)))
            .ToList();
    }

    public Task<CompareResult> CompareAsync(PrIdentity id, string previousHeadSha, string currentHeadSha, CancellationToken ct)
    {
        // ADO force-push detection requires an extra git diff call; defer to a later iteration.
        // For now we return "no force push" with zero counts — callers tolerate the default.
        if (string.Equals(previousHeadSha, currentHeadSha, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new CompareResult(false, 0, 0));
        }
        return Task.FromResult(new CompareResult(false, 0, 0));
    }

    // ----------------------------------------------------------------------
    // Internals.
    // ----------------------------------------------------------------------

    private async Task<string> ResolveReviewerIdAsync(CancellationToken ct)
    {
        if (_cachedReviewerId is not null)
        {
            return _cachedReviewerId;
        }
        var profile = await _client.GetMyProfileAsync(ct);
        if (string.IsNullOrWhiteSpace(profile.Id))
        {
            throw new InvalidOperationException(
                "Azure DevOps profile API returned no user id. " +
                "Run `az login` against the tenant that owns this org.");
        }
        _cachedReviewerId = profile.Id;
        _logger.LogDebug("Resolved ADO reviewer id for {Org}/{Project}: {Id}", _org, _project, profile.Id);
        return profile.Id;
    }

    private RemotePullRequest? MapListItem(AdoDtos.PullRequest pr)
    {
        if (pr.Repository is null || string.IsNullOrEmpty(pr.Repository.Id))
        {
            _logger.LogWarning("ADO PR {Id} missing repository.id; skipping.", pr.PullRequestId);
            return null;
        }

        var repoName = pr.Repository.Name;
        var projectName = pr.Repository.Project?.Name ?? _project;
        var canonicalUrl = PrIdentity.FormatAdoUrl(_org, projectName, repoName, pr.PullRequestId);

        Guid.TryParse(pr.Repository.Id, out var repoGuid);
        Guid.TryParse(pr.Repository.Project?.Id, out var projectGuid);
        var stable = PrIdentity.FormatAdoStable(_org, projectGuid, repoGuid, pr.PullRequestId);

        return new RemotePullRequest(
            Identity: new PrIdentity(canonicalUrl, stable),
            SourceKind: SourceKind.AzureDevOps,
            SourceId: SourceId,
            DisplayRepo: $"{projectName}/{repoName}",
            Number: pr.PullRequestId,
            Title: pr.Title,
            AuthorLogin: pr.CreatedBy?.UniqueName ?? pr.CreatedBy?.DisplayName,
            Url: canonicalUrl,
            Status: MapStatus(pr.Status),
            LastUpdated: pr.CreationDate); // ADO list endpoint doesn't return lastUpdated; CreationDate is best available.
    }

    private RemotePullRequestDetail MapDetail(PrIdentity id, AdoDtos.PullRequest pr, IReadOnlyList<AdoDtos.Commit> commits)
    {
        var headSha = pr.LastMergeSourceCommit?.CommitId ?? string.Empty;
        var baseSha = pr.LastMergeTargetCommit?.CommitId ?? string.Empty;
        var mergeBase = pr.LastMergeCommit?.CommitId;

        var orderedShas = commits.Select(c => c.CommitId).ToList();

        var reviewerState = InterpretReviewerState(pr.Reviewers, _cachedReviewerId);

        // ADO mergeStatus values: succeeded, conflicts, queued, rejectedByPolicy,
        // failure, notSet. Map to a lowercase short string for the brief.
        var mergeable = string.IsNullOrEmpty(pr.MergeStatus) ? null : pr.MergeStatus.ToLowerInvariant();

        return new RemotePullRequestDetail(
            Identity: id,
            HeadSha: headSha,
            BaseSha: baseSha,
            MergeBaseSha: mergeBase,
            OrderedCommitShas: orderedShas,
            ReviewerState: reviewerState,
            Status: MapStatus(pr.Status),
            RawMetadataJson: JsonSerializer.Serialize(new
            {
                pr.PullRequestId,
                pr.Status,
                pr.MergeStatus,
                pr.IsDraft,
                pr.SourceRefName,
                pr.TargetRefName,
                head = headSha,
                @base = baseSha,
                mergeBase,
                repo = new { id = pr.Repository?.Id, name = pr.Repository?.Name, project = pr.Repository?.Project?.Name },
            }),
            Body: pr.Description,
            // ADO file-change listing requires a separate /iterations/changes call
            // per iteration — heavier than GitHub's single endpoint. Deferred for
            // v0.2; the brief renders "_File list unavailable for this source._"
            // when Files is null.
            Files: null,
            MergeableState: mergeable,
            // ADO has no single combined CI status; policy evaluations + build
            // results are separate calls. Deferred for v0.2.
            CiStatus: null);
    }

    private IReadOnlyList<RemoteThread> MapThreads(IReadOnlyList<AdoDtos.Thread> threads)
    {
        var result = new List<RemoteThread>(threads.Count);
        foreach (var t in threads)
        {
            if (t.IsDeleted) continue;
            if (t.Comments.Count == 0) continue;

            // Filter out system-generated "vote update" threads — they have a
            // single comment with CommentType="system". Surface only human/bot
            // conversation.
            if (t.Comments.All(c => string.Equals(c.CommentType, "system", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var first = t.Comments.First(c => !string.Equals(c.CommentType, "system", StringComparison.OrdinalIgnoreCase));
            var (isBot, kind) = ClassifyAuthor(first.Author);
            var resolved = string.Equals(t.Status, "fixed", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(t.Status, "closed", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(t.Status, "wontFix", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(t.Status, "byDesign", StringComparison.OrdinalIgnoreCase);

            var anchorLine = t.ThreadContext?.RightFileStart?.Line > 0
                ? t.ThreadContext.RightFileStart.Line
                : (t.ThreadContext?.LeftFileStart?.Line > 0 ? t.ThreadContext.LeftFileStart.Line : (int?)null);

            result.Add(new RemoteThread(
                PlatformThreadId: $"ado-thread:{t.Id}",
                Kind: ThreadKind.AdoThread,
                AuthorLogin: first.Author?.UniqueName ?? first.Author?.DisplayName,
                IsBot: isBot,
                BotKind: kind,
                IsResolved: resolved,
                CreatedAt: t.PublishedDate ?? first.PublishedDate,
                LastUpdatedAt: t.LastUpdatedDate ?? first.LastUpdatedDate ?? first.PublishedDate,
                RawJson: JsonSerializer.Serialize(new
                {
                    id = t.Id,
                    status = t.Status,
                    filePath = t.ThreadContext?.FilePath,
                    commentCount = t.Comments.Count,
                }),
                BodyExcerpt: TruncateExcerpt(first.Content),
                AnchorPath: t.ThreadContext?.FilePath,
                AnchorLine: anchorLine));
        }
        return result;
    }

    /// <summary>Single-line excerpt of a comment body capped at 240 chars.</summary>
    private static string? TruncateExcerpt(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var collapsed = System.Text.RegularExpressions.Regex.Replace(body, @"\s+", " ").Trim();
        return collapsed.Length <= 240 ? collapsed : collapsed[..240] + "…";
    }

    private (bool IsBot, BotKind? BotKind) ClassifyAuthor(AdoDtos.Identity? author)
    {
        if (author is null) return (false, null);
        var login = author.UniqueName ?? author.DisplayName ?? string.Empty;
        return _botDetector.Classify(login, reportedTypeIsBot: author.IsContainer);
    }

    private static ReviewerState? InterpretReviewerState(IReadOnlyList<AdoDtos.Reviewer> reviewers, string? selfId)
    {
        if (string.IsNullOrEmpty(selfId))
        {
            return ReviewerState.Requested;
        }
        var self = reviewers.FirstOrDefault(r => string.Equals(r.Id, selfId, StringComparison.OrdinalIgnoreCase));
        if (self is null)
        {
            return ReviewerState.Requested;
        }
        return self.Vote switch
        {
            10 => ReviewerState.Approved,
            5 => ReviewerState.ApprovedWithSuggestions,
            -5 => ReviewerState.Waiting,
            -10 => ReviewerState.ChangesRequested,
            _ => ReviewerState.Requested,
        };
    }

    private static PullRequestStatus MapStatus(string? status)
    {
        return status?.ToLowerInvariant() switch
        {
            "active" => PullRequestStatus.Open,
            "completed" => PullRequestStatus.Merged,
            "abandoned" => PullRequestStatus.Closed,
            _ => PullRequestStatus.Open,
        };
    }

    /// <summary>Extract (repoId, prId) from a canonical ADO URL.</summary>
    /// <remarks>
    /// ADO URLs do not carry the repo id; they carry the repo *name*. The
    /// ADO REST PR-detail endpoint accepts either repo GUID or repo name, so
    /// the name from the URL works directly.
    /// </remarks>
    internal static (string repoId, int prId) ParseAdoUrl(string url)
    {
        var c = PrUrl.Parse(url);
        if (c.Platform != PrPlatform.AzureDevOps)
        {
            throw new FormatException($"AzureDevOpsReadSource cannot handle non-ADO URL '{url}'.");
        }
        return (c.Repo, c.Number);
    }

    private static string FirstLineOf(string s)
    {
        var nl = s.IndexOf('\n');
        return nl < 0 ? s : s[..nl];
    }
}
