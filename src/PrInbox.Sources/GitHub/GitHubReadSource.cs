using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit;
using PrInbox.Core.Credentials;
using PrInbox.Core.Models;
using CompareResult = PrInbox.Core.Models.CompareResult;
using OctokitCompare = Octokit.CompareResult;

namespace PrInbox.Sources.GitHub;

/// <summary>
/// Read-only source adapter for GitHub.com and GitHub Enterprise. Identical
/// implementation for both — they differ only in the <c>baseUri</c> of the
/// underlying Octokit client.
/// </summary>
public sealed class GitHubReadSource : IPrReadSource
{
    private readonly string _hostname;
    private readonly bool _isEnterprise;
    private readonly ITokenProvider _tokenProvider;
    private readonly BotDetector _botDetector;
    private readonly ILogger<GitHubReadSource> _logger;

    public GitHubReadSource(
        string sourceId,
        string hostname,
        bool isEnterprise,
        ITokenProvider tokenProvider,
        BotDetector? botDetector = null,
        ILogger<GitHubReadSource>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        SourceId = sourceId;
        _hostname = hostname;
        _isEnterprise = isEnterprise;
        _tokenProvider = tokenProvider;
        _botDetector = botDetector ?? new BotDetector();
        _logger = logger ?? NullLogger<GitHubReadSource>.Instance;
    }

    public string SourceId { get; }

    public SourceKind Kind => _isEnterprise ? SourceKind.GitHubEnterprise : SourceKind.GitHub;

    public SourceCapabilities Capabilities { get; } = new(
        SupportsGlobalReviewerInbox: true,
        SupportsThreadResolution: true,
        SupportsBotAuthorClassification: true,
        SupportsReviewRequestTimestamps: true,
        SupportsStableRepoIds: true,
        SupportsForcePushDetection: true);

    public async IAsyncEnumerable<RemotePullRequest> ListAssignedFastAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var client = await CreateClientAsync(ct);
        var page = 1;
        const int perPage = 50;
        var emitted = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var req = new SearchIssuesRequest("is:pr is:open review-requested:@me")
            {
                PerPage = perPage,
                Page = page,
            };
            SearchIssuesResult searchResult;
            try
            {
                searchResult = await client.Search.SearchIssues(req);
            }
            catch (RateLimitExceededException ex)
            {
                _logger.LogWarning(ex, "GitHub search rate limited at page {Page}.", page);
                throw;
            }

            foreach (var item in searchResult.Items)
            {
                yield return MapSearchItem(item);
                emitted++;
            }

            if (searchResult.Items.Count < perPage || emitted >= searchResult.TotalCount)
            {
                yield break;
            }
            page++;
            if (page > 20)
            {
                _logger.LogWarning("Hit safety cap of 20 pages while paging review inbox.");
                yield break;
            }
        }
    }

    public async Task<PrEnrichmentBundle> EnrichAsync(PrIdentity id, CancellationToken ct)
    {
        var detail = await FetchDetailAsync(id, ct);
        var threads = await FetchThreadsAsync(id, ct);
        return new PrEnrichmentBundle(detail, threads);
    }

    private async Task<RemotePullRequestDetail> FetchDetailAsync(PrIdentity id, CancellationToken ct)
    {
        var (owner, repo, number) = ParseUrl(id.Url);
        var client = await CreateClientAsync(ct);

        var pr = await client.PullRequest.Get(owner, repo, number);

        var commits = await client.PullRequest.Commits(owner, repo, number);
        var orderedShas = commits.Reverse().Select(c => c.Sha).ToList();

        ReviewerState? reviewerState = null;
        try
        {
            var reviews = await client.PullRequest.Review.GetAll(owner, repo, number);
            reviewerState = InterpretReviewerState(reviews, ownLoginHint: null);
        }
        catch
        {
            // Reviews API can 422 on draft PRs in some edge cases; reviewer
            // state is best-effort and defaults to Requested if the PR is in
            // the search inbox.
            reviewerState = ReviewerState.Requested;
        }

        // Files — cheap (one paged endpoint) and fundamental to the brief.
        IReadOnlyList<RemoteFileChange>? files = null;
        try
        {
            var prFiles = await client.PullRequest.Files(owner, repo, number);
            files = prFiles
                .Select(f => new RemoteFileChange(
                    Path: f.FileName,
                    Additions: f.Additions,
                    Deletions: f.Deletions,
                    Status: f.Status))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Files endpoint failed for {Url}; brief will show 'files unavailable'.", id.Url);
        }

        // CI / combined status — best-effort. Some repos use check-runs
        // exclusively (Status returns Pending for those); brief falls back to
        // 'unknown' if both calls fail.
        string? ciStatus = null;
        try
        {
            var combined = await client.Repository.Status.GetCombined(owner, repo, pr.Head.Sha);
            var stateValue = combined.State.StringValue;
            if (!string.IsNullOrEmpty(stateValue))
            {
                ciStatus = stateValue.ToLowerInvariant();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Combined status failed for {Url}; CI status will be null.", id.Url);
        }

        var mergeable = pr.MergeableState?.StringValue?.ToLowerInvariant()
            ?? (pr.Mergeable == true ? "clean"
                : pr.Mergeable == false ? "conflicts"
                : null);

        return new RemotePullRequestDetail(
            Identity: id,
            HeadSha: pr.Head.Sha,
            BaseSha: pr.Base.Sha,
            MergeBaseSha: null,
            OrderedCommitShas: orderedShas,
            ReviewerState: reviewerState,
            Status: MapPrStatus(pr),
            RawMetadataJson: System.Text.Json.JsonSerializer.Serialize(new
            {
                pr.Id,
                pr.NodeId,
                pr.Number,
                pr.State,
                pr.Title,
                pr.Draft,
                pr.UpdatedAt,
                head = new { pr.Head.Sha, pr.Head.Ref },
                @base = new { pr.Base.Sha, pr.Base.Ref },
                repo = new { pr.Base.Repository.Id, pr.Base.Repository.FullName },
            }),
            Body: pr.Body,
            Files: files,
            MergeableState: mergeable,
            CiStatus: ciStatus);
    }

    private async Task<IReadOnlyList<RemoteThread>> FetchThreadsAsync(PrIdentity id, CancellationToken ct)
    {
        var (owner, repo, number) = ParseUrl(id.Url);
        var client = await CreateClientAsync(ct);

        var result = new List<RemoteThread>();

        // Inline review comments.
        var reviewComments = await client.PullRequest.ReviewComment.GetAll(owner, repo, number);
        foreach (var c in reviewComments)
        {
            var (isBot, kind) = ClassifyAuthor(c.User);
            result.Add(new RemoteThread(
                PlatformThreadId: $"review-comment:{c.Id}",
                Kind: ThreadKind.ReviewComment,
                AuthorLogin: c.User?.Login,
                IsBot: isBot,
                BotKind: kind,
                IsResolved: false, // GraphQL exposes resolution; REST does not. Best-effort.
                CreatedAt: c.CreatedAt,
                LastUpdatedAt: c.UpdatedAt,
                RawJson: $"{{\"id\":{c.Id},\"path\":\"{Escape(c.Path)}\"}}",
                BodyExcerpt: TruncateExcerpt(c.Body),
                AnchorPath: c.Path,
                // Octokit 14 surfaces `Position` (line-in-diff, falls back to
                // OriginalPosition when outdated). Good enough orientation for
                // the brief; reviewers click through to the GitHub UI for the
                // actual file line.
                AnchorLine: c.Position > 0 ? c.Position : (c.OriginalPosition > 0 ? c.OriginalPosition : (int?)null)));
        }

        // Top-level issue comments on the PR.
        var issueComments = await client.Issue.Comment.GetAllForIssue(owner, repo, number);
        foreach (var c in issueComments)
        {
            var (isBot, kind) = ClassifyAuthor(c.User);
            result.Add(new RemoteThread(
                PlatformThreadId: $"issue-comment:{c.Id}",
                Kind: ThreadKind.IssueComment,
                AuthorLogin: c.User?.Login,
                IsBot: isBot,
                BotKind: kind,
                IsResolved: false,
                CreatedAt: c.CreatedAt,
                LastUpdatedAt: c.UpdatedAt ?? c.CreatedAt,
                RawJson: $"{{\"id\":{c.Id}}}",
                BodyExcerpt: TruncateExcerpt(c.Body),
                AnchorPath: null,
                AnchorLine: null));
        }

        // Top-level review bodies.
        var reviews = await client.PullRequest.Review.GetAll(owner, repo, number);
        foreach (var r in reviews)
        {
            if (string.IsNullOrEmpty(r.Body)) continue;
            var (isBot, kind) = ClassifyAuthor(r.User);
            result.Add(new RemoteThread(
                PlatformThreadId: $"review-body:{r.Id}",
                Kind: ThreadKind.ReviewBody,
                AuthorLogin: r.User?.Login,
                IsBot: isBot,
                BotKind: kind,
                IsResolved: false,
                CreatedAt: r.SubmittedAt,
                LastUpdatedAt: r.SubmittedAt,
                RawJson: $"{{\"id\":{r.Id},\"state\":\"{r.State}\"}}",
                BodyExcerpt: TruncateExcerpt(r.Body),
                AnchorPath: null,
                AnchorLine: null));
        }

        return result;
    }

    /// <summary>
    /// Trim a comment body to a single-line excerpt suitable for the brief.
    /// Strips CR/LF, collapses runs of whitespace, and caps at 240 chars
    /// (with a trailing ellipsis if truncated).
    /// </summary>
    private static string? TruncateExcerpt(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var collapsed = System.Text.RegularExpressions.Regex.Replace(body, @"\s+", " ").Trim();
        return collapsed.Length <= 240 ? collapsed : collapsed[..240] + "…";
    }

    public async Task<IReadOnlyList<RemoteCommit>> GetCommitsAsync(PrIdentity id, CancellationToken ct)
    {
        var (owner, repo, number) = ParseUrl(id.Url);
        var client = await CreateClientAsync(ct);
        var commits = await client.PullRequest.Commits(owner, repo, number);
        return commits
            .Reverse() // newest-first
            .Select(c => new RemoteCommit(
                Sha: c.Sha,
                AuthorLogin: c.Author?.Login ?? c.Commit?.Author?.Name,
                CommittedAt: c.Commit?.Author?.Date ?? DateTimeOffset.UtcNow,
                Subject: FirstLineOf(c.Commit?.Message ?? string.Empty)))
            .ToList();
    }

    public async Task<CompareResult> CompareAsync(PrIdentity id, string previousHeadSha, string currentHeadSha, CancellationToken ct)
    {
        if (previousHeadSha == currentHeadSha)
        {
            return new CompareResult(false, 0, 0);
        }

        var (owner, repo, _) = ParseUrl(id.Url);
        var client = await CreateClientAsync(ct);

        try
        {
            var compare = await client.Repository.Commit.Compare(owner, repo, previousHeadSha, currentHeadSha);
            // status "diverged" indicates the prior SHA is no longer on the line of history.
            var forcePushed = string.Equals(compare.Status, "diverged", StringComparison.OrdinalIgnoreCase);
            return new CompareResult(
                BaseUnreachableFromHead: forcePushed,
                CommitsAhead: compare.AheadBy,
                CommitsBehind: compare.BehindBy);
        }
        catch (NotFoundException)
        {
            // Previous SHA is gone entirely — definitive force-push signal.
            return new CompareResult(BaseUnreachableFromHead: true, CommitsAhead: 0, CommitsBehind: 1);
        }
    }

    // ----------------------------------------------------------------------
    // Internals.
    // ----------------------------------------------------------------------

    private async Task<GitHubClient> CreateClientAsync(CancellationToken ct)
    {
        var token = await _tokenProvider.GetTokenAsync(ct);
        var product = new ProductHeaderValue("pr-inbox", "0.1");
        var client = _isEnterprise
            ? new GitHubClient(product, new Uri($"https://{_hostname}/api/v3/"))
            : new GitHubClient(product);
        client.Credentials = new Credentials(token);
        return client;
    }

    private RemotePullRequest MapSearchItem(Issue item)
    {
        var canonicalUrl = PrUrl.Canonicalize(item.HtmlUrl);
        var components = PrUrl.Parse(canonicalUrl);
        var stable = _isEnterprise
            ? PrIdentity.FormatGheStable(_hostname, item.Repository?.Id ?? 0L, item.Id)
            : PrIdentity.FormatGitHubStable(item.Repository?.Id ?? 0L, item.Id);

        return new RemotePullRequest(
            Identity: new PrIdentity(canonicalUrl, stable),
            SourceKind: Kind,
            SourceId: SourceId,
            DisplayRepo: $"{components.Owner}/{components.Repo}",
            Number: item.Number,
            Title: item.Title,
            AuthorLogin: item.User?.Login,
            Url: canonicalUrl,
            Status: item.State.Value == ItemState.Open ? PullRequestStatus.Open : PullRequestStatus.Closed,
            LastUpdated: item.UpdatedAt ?? item.CreatedAt);
    }

    /// <summary>
    /// Convenience: parse a canonical PR URL into (owner, repo, number) for
    /// the GitHub REST API. Throws if <paramref name="url"/> is not a
    /// recognized GitHub/GHE URL.
    /// </summary>
    internal static (string owner, string repo, int number) ParseUrl(string url)
    {
        var c = PrUrl.Parse(url);
        if (c.Platform == PrPlatform.AzureDevOps)
            throw new FormatException($"GitHubReadSource cannot handle ADO URL '{url}'.");
        return (c.Owner, c.Repo, c.Number);
    }

    private static PullRequestStatus MapPrStatus(PullRequest pr)
    {
        if (pr.Merged) return PullRequestStatus.Merged;
        return pr.State.Value switch
        {
            ItemState.Open => PullRequestStatus.Open,
            ItemState.Closed => PullRequestStatus.Closed,
            _ => PullRequestStatus.Open,
        };
    }

    private static ReviewerState InterpretReviewerState(IReadOnlyList<PullRequestReview> reviews, string? ownLoginHint)
    {
        // Without the active login, we can't tell whose review is whose. We
        // default to Commented if any reviews exist, else Requested. v0.2 can
        // refine by reading the active gh user once at startup.
        if (reviews.Count == 0) return ReviewerState.Requested;
        return ReviewerState.Commented;
    }

    private (bool IsBot, BotKind? BotKind) ClassifyAuthor(User? user)
    {
        if (user is null) return (false, null);
        var reportedBot = user.Type.HasValue && user.Type.Value == AccountType.Bot;
        return _botDetector.Classify(user.Login, reportedBot);
    }

    private static string Escape(string? value) =>
        string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string FirstLineOf(string message)
    {
        var nl = message.IndexOf('\n');
        return nl < 0 ? message : message[..nl];
    }
}
