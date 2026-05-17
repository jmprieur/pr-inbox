using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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

    /// <summary>
    /// The two GitHub search qualifiers we union to build the inbox. They
    /// must be issued as separate queries because the Issues Search API
    /// does not accept boolean OR between user qualifiers
    /// (<c>review-requested:</c> and <c>reviewed-by:</c>).
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><c>review-requested:@me</c> — PR is currently pending your
    ///     review (you appear in the requested-reviewers list).</item>
    ///   <item><c>reviewed-by:@me</c> — you have already submitted at least
    ///     one review (any kind: comment / approve / request changes).
    ///     GitHub removes you from the requested-reviewers list the moment
    ///     you submit a review, so without this second query the PR would
    ///     drop out of the inbox right when it needs the most follow-up.
    ///   </item>
    /// </list>
    /// Both are scoped to <c>is:pr is:open</c>; merged/closed PRs fall out
    /// naturally.
    /// </remarks>
    internal static readonly string[] InboxQueries = new[]
    {
        "is:pr is:open review-requested:@me",
        "is:pr is:open reviewed-by:@me",
    };

    public async IAsyncEnumerable<RemotePullRequest> ListAssignedFastAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var client = await CreateClientAsync(ct);
        // Dedupe across the two queries: a PR you've been re-requested on
        // after reviewing it appears in both result sets. URL is the canonical
        // identity at this point in the pipeline.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var query in InboxQueries)
        {
            ct.ThrowIfCancellationRequested();
            await foreach (var pr in SearchPrsPagedAsync(client, query, ct))
            {
                if (seen.Add(pr.Url))
                {
                    yield return pr;
                }
            }
        }
    }

    private async IAsyncEnumerable<RemotePullRequest> SearchPrsPagedAsync(
        IGitHubClient client,
        string query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var page = 1;
        const int perPage = 50;
        var emitted = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var req = new SearchIssuesRequest(query)
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
                _logger.LogWarning(ex, "GitHub search rate limited at page {Page} for {Query}.", page, query);
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
                _logger.LogWarning("Hit safety cap of 20 pages while paging {Query}.", query);
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

    private static readonly HttpClient s_graphqlHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private async Task<IReadOnlyList<RemoteThread>> FetchThreadsAsync(PrIdentity id, CancellationToken ct)
    {
        var (owner, repo, number) = ParseUrl(id.Url);
        var client = await CreateClientAsync(ct);

        var result = new List<RemoteThread>();

        // GraphQL pass: build a map from REST review-comment database id →
        // (thread node id, thread isResolved). We need this to (a) populate
        // platform_thread_node_id so the /threads UI can call
        // resolveReviewThread, and (b) override the hard-coded
        // IsResolved=false on REST review comments below — REST has no
        // notion of thread resolution; only GraphQL does.
        Dictionary<long, ThreadGraphInfo> threadMap;
        try
        {
            threadMap = await FetchReviewThreadMapAsync(owner, repo, number, ct);
        }
        catch (Exception ex)
        {
            // Don't fail the whole enrich if GraphQL is unavailable (e.g.
            // GHE outage, network glitch). The REST path still produces
            // useful rows; node ids will get backfilled on next sync.
            _logger.LogWarning(ex,
                "GraphQL review-thread fetch failed for {Owner}/{Repo}#{N}; falling back to REST-only thread enrichment",
                owner, repo, number);
            threadMap = new Dictionary<long, ThreadGraphInfo>();
        }

        // Inline review comments.
        var reviewComments = await client.PullRequest.ReviewComment.GetAll(owner, repo, number);
        foreach (var c in reviewComments)
        {
            var (isBot, kind) = ClassifyAuthor(c.User);
            var graph = threadMap.TryGetValue(c.Id, out var g) ? g : default;
            result.Add(new RemoteThread(
                PlatformThreadId: $"review-comment:{c.Id}",
                Kind: ThreadKind.ReviewComment,
                AuthorLogin: c.User?.Login,
                IsBot: isBot,
                BotKind: kind,
                IsResolved: graph.NodeId is not null && graph.IsResolved,
                CreatedAt: c.CreatedAt,
                LastUpdatedAt: c.UpdatedAt,
                RawJson: $"{{\"id\":{c.Id},\"path\":\"{Escape(c.Path)}\"}}",
                BodyExcerpt: TruncateExcerpt(c.Body),
                AnchorPath: c.Path,
                // Octokit 14 surfaces `Position` (line-in-diff, falls back to
                // OriginalPosition when outdated). Good enough orientation for
                // the brief; reviewers click through to the GitHub UI for the
                // actual file line.
                AnchorLine: c.Position > 0 ? c.Position : (c.OriginalPosition > 0 ? c.OriginalPosition : (int?)null),
                PlatformThreadNodeId: graph.NodeId));
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
    /// Output of <see cref="FetchReviewThreadMapAsync"/>: maps a REST review-
    /// comment database id to the GraphQL thread that contains it, plus that
    /// thread's authoritative <c>isResolved</c>. A single GraphQL thread may
    /// contain many REST comments (root + replies); the same record is
    /// returned for each member comment so callers can join 1:1.
    /// </summary>
    private readonly record struct ThreadGraphInfo(string? NodeId, bool IsResolved);

    /// <summary>
    /// GraphQL query for review threads on a single PR. We fetch all threads
    /// and their member comments' <c>databaseId</c> so we can join back to
    /// the REST <c>id</c> field used by the REST review-comments endpoint.
    /// </summary>
    private const string ReviewThreadsQuery = """
        query($owner:String!, $name:String!, $number:Int!, $after:String) {
          repository(owner:$owner, name:$name) {
            pullRequest(number:$number) {
              reviewThreads(first:100, after:$after) {
                pageInfo { hasNextPage endCursor }
                nodes {
                  id
                  isResolved
                  comments(first:100) {
                    nodes { databaseId }
                  }
                }
              }
            }
          }
        }
        """;

    private async Task<Dictionary<long, ThreadGraphInfo>> FetchReviewThreadMapAsync(
        string owner,
        string repo,
        int number,
        CancellationToken ct)
    {
        var token = await _tokenProvider.GetTokenAsync(ct);
        var endpoint = _isEnterprise
            ? new Uri($"https://{_hostname}/api/graphql")
            : new Uri("https://api.github.com/graphql");

        var map = new Dictionary<long, ThreadGraphInfo>();
        string? cursor = null;

        // Hard cap to avoid pathological pagination on a runaway PR.
        for (var page = 0; page < 20; page++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("pr-inbox", "0.2"));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Content = JsonContent.Create(new
            {
                query = ReviewThreadsQuery,
                variables = new { owner, name = repo, number, after = cursor },
            });

            using var resp = await s_graphqlHttp.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (doc.RootElement.TryGetProperty("errors", out var errs) && errs.GetArrayLength() > 0)
            {
                throw new InvalidOperationException(
                    $"GitHub GraphQL returned errors for {owner}/{repo}#{number}: {errs}");
            }

            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object)
            {
                break;
            }

            var threads = data
                .GetProperty("repository")
                .GetProperty("pullRequest")
                .GetProperty("reviewThreads");

            foreach (var thread in threads.GetProperty("nodes").EnumerateArray())
            {
                var nodeId = thread.GetProperty("id").GetString();
                var isResolved = thread.GetProperty("isResolved").GetBoolean();
                var info = new ThreadGraphInfo(nodeId, isResolved);

                if (thread.TryGetProperty("comments", out var comments)
                    && comments.TryGetProperty("nodes", out var commentNodes))
                {
                    foreach (var comment in commentNodes.EnumerateArray())
                    {
                        if (comment.TryGetProperty("databaseId", out var dbid)
                            && dbid.ValueKind == JsonValueKind.Number
                            && dbid.TryGetInt64(out var id))
                        {
                            // Same info repeated for every comment in the thread;
                            // overwriting is harmless since the value is identical.
                            map[id] = info;
                        }
                    }
                }
            }

            var pageInfo = threads.GetProperty("pageInfo");
            if (!pageInfo.GetProperty("hasNextPage").GetBoolean()) break;
            cursor = pageInfo.GetProperty("endCursor").GetString();
            if (string.IsNullOrEmpty(cursor)) break;
        }

        return map;
    }

    /// <summary>
    /// Trim a comment body to a single-line excerpt suitable for the brief.
    /// Strips CR/LF, collapses runs of whitespace, and caps at 240 chars
    /// (with a trailing ellipsis if truncated).
    /// </summary>
    private static string? TruncateExcerpt(string? body)
    {
        // 1024 chars (was 240): reviewers need enough text to dedupe their
        // findings against existing bot comments without an extra round trip
        // back to the GitHub API. The brief is the primary cost driver, and
        // a few KB extra per thread is cheaper than a `gh api` call per dedupe.
        if (string.IsNullOrWhiteSpace(body)) return null;
        var collapsed = System.Text.RegularExpressions.Regex.Replace(body, @"\s+", " ").Trim();
        return collapsed.Length <= 1024 ? collapsed : collapsed[..1024] + "…";
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
        var product = new Octokit.ProductHeaderValue("pr-inbox", "0.1");
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
