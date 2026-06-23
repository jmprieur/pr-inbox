using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PrInbox.Core.Credentials;

namespace PrInbox.Publishers;

/// <summary>
/// Posts review findings to GitHub.com or GitHub Enterprise. One instance
/// per (host, identity) tuple.
/// </summary>
/// <remarks>
/// Wire format: <c>POST {api-base}/repos/{owner}/{repo}/pulls/{N}/reviews</c>
/// with body
/// <code>
/// {
///   "commit_id": "abc...",
///   "event": "COMMENT",
///   "body": "&lt;header&gt;",
///   "comments": [
///     { "path": "src/Foo.cs", "line": 42, "side": "RIGHT", "body": "..." }
///   ]
/// }
/// </code>
/// Dry-run returns immediately with a plan; no HTTP call is made.
/// </remarks>
public sealed class GitHubReviewPublisher : IPrReviewPublisher
{
    private readonly ITokenProvider _tokens;
    private readonly HttpClient _http;
    private readonly bool _isEnterprise;
    private readonly string _host;
    private readonly string _identityUsed;
    private readonly ILogger<GitHubReviewPublisher> _log;

    /// <summary>
    /// Construct with a caller-supplied <see cref="HttpClient"/> (tests
    /// inject a mock handler; production code passes a singleton).
    /// </summary>
    /// <param name="host">e.g. <c>github.com</c> or <c>ghe.example.com</c>.</param>
    /// <param name="identityUsed">Logical identity name (jmprieur, jmprieur_microsoft, …).</param>
    public GitHubReviewPublisher(
        ITokenProvider tokens,
        HttpClient http,
        bool isEnterprise,
        string host,
        string identityUsed,
        ILogger<GitHubReviewPublisher> log)
    {
        _tokens = tokens;
        _http = http;
        _isEnterprise = isEnterprise;
        _host = host;
        _identityUsed = identityUsed;
        _log = log;
    }

    public string Kind => _isEnterprise ? "github-enterprise" : "github";

    public async Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken ct)
    {
        // For a plain "comment" review, the user must pick at least one
        // finding (otherwise we'd post a review with nothing in it). For an
        // explicit approve / request-changes vote, an empty selection is
        // legitimate — the review body header is the carrier.
        if (request.Findings.Count == 0 && request.Event == ReviewEvent.Comment)
        {
            return PublishResult.Failure(_identityUsed, "No findings selected.");
        }

        PrUrlRef target;
        try { target = PullRequestUrlParser.Parse(request.PrUrl); }
        catch (ArgumentException ex)
        {
            return PublishResult.Failure(_identityUsed, ex.Message);
        }

        if (target.Kind == PrPlatform.AzureDevOps)
        {
            return PublishResult.Failure(_identityUsed,
                $"This publisher handles GitHub, not Azure DevOps URLs ({request.PrUrl}).");
        }

        // Split anchorable vs non-anchorable. Non-anchorable findings land
        // in the review body so they're never silently dropped.
        var anchorable = request.Findings.Where(f => f.DiffAnchorable).ToList();
        var nonAnchorable = request.Findings.Where(f => !f.DiffAnchorable).ToList();
        var reviewBody = PublishHelpers.ComposeReviewBody(
            request.ReviewBodyHeader, nonAnchorable, request.HeadShaAtAuthoring);

        // DRY RUN: no network at all.
        if (request.DryRun)
        {
            return PublishResult.DryRunPlan(
                inlineCount: anchorable.Count,
                bodyOnlyCount: nonAnchorable.Count,
                skipped: 0,
                identityUsed: _identityUsed,
                warning: $"Dry-run: would POST {MapEvent(request.Event)} to {ApiBase(target)}/repos/{target.Owner}/{target.Repo}/pulls/{target.Number}/reviews as '{_identityUsed}'.");
        }

        // Live path.
        string token;
        try { token = await _tokens.GetTokenAsync(ct); }
        catch (Exception ex)
        {
            return PublishResult.Failure(_identityUsed, $"Token acquisition failed: {ex.Message}");
        }

        // HEAD check (optional, governed by ValidateRemoteState).
        var headSha = request.HeadShaAtAuthoring;
        var headChanged = false;
        var warnings = new List<string>();
        if (request.ValidateRemoteState)
        {
            var fresh = await TryGetCurrentHeadAsync(token, target, ct);
            if (fresh is not null && !fresh.Equals(request.HeadShaAtAuthoring, StringComparison.OrdinalIgnoreCase))
            {
                headSha = fresh;
                headChanged = true;
                warnings.Add($"HEAD changed since the brief was built: {PublishHelpers.ShortSha(request.HeadShaAtAuthoring)} -> {PublishHelpers.ShortSha(fresh)}. Inline comments will target the new HEAD.");
            }
        }

        var inlinePayloads = anchorable.Select(BuildInlinePayload).ToArray();
        var body = new ReviewBody
        {
            CommitId = headSha,
            Event = MapEvent(request.Event),
            Body = reviewBody,
            Comments = inlinePayloads,
        };

        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"{ApiBase(target)}/repos/{target.Owner}/{target.Repo}/pulls/{target.Number}/reviews");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("pr-inbox", "0.1"));
        req.Content = JsonContent.Create(body, options: JsonOpts);

        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, ct); }
        catch (Exception ex)
        {
            return PublishResult.Failure(_identityUsed, $"HTTP send failed: {ex.Message}");
        }

        var respText = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var hint = resp.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Authentication failed. The token for this identity may be expired.",
                HttpStatusCode.Forbidden => "Forbidden. This identity may lack write access to this PR.",
                HttpStatusCode.NotFound => "PR not found. URL or identity may be wrong.",
                HttpStatusCode.UnprocessableEntity => "Validation error from GitHub (often inline anchor outside the diff).",
                _ => null,
            };
            var msg = hint is null
                ? $"GitHub API returned {(int)resp.StatusCode}: {respText}"
                : $"GitHub API returned {(int)resp.StatusCode} ({hint}): {respText}";
            _log.LogWarning("GitHub publish failed for {Url}: {Msg}", request.PrUrl, msg);
            return PublishResult.Failure(_identityUsed, msg);
        }

        ReviewResponse? created;
        try { created = JsonSerializer.Deserialize<ReviewResponse>(respText, JsonOpts); }
        catch (JsonException ex)
        {
            return PublishResult.Failure(_identityUsed, $"GitHub API returned 2xx but body could not be parsed: {ex.Message}");
        }

        if (created is null || string.IsNullOrEmpty(created.IdString))
        {
            return PublishResult.Failure(_identityUsed, "GitHub API returned 2xx without a review id.");
        }

        return new PublishResult(
            Posted: true,
            PlatformReviewId: created.IdString,
            ReviewUrl: created.HtmlUrl,
            InlineCount: anchorable.Count,
            BodyOnlyCount: nonAnchorable.Count,
            SkippedAsAlreadyPosted: 0,
            HeadShaAtPost: headSha,
            HeadChanged: headChanged,
            IdentityUsed: _identityUsed,
            Warnings: warnings,
            Errors: Array.Empty<string>());
    }

    private string ApiBase(PrUrlRef target) =>
        _isEnterprise ? $"https://{target.Host}/api/v3" : "https://api.github.com";

    /// <summary>
    /// GraphQL endpoint. GitHub.com uses <c>api.github.com/graphql</c>;
    /// GHE uses <c>{host}/api/graphql</c> (note: NOT <c>/api/v3/graphql</c>,
    /// which is the REST namespace).
    /// </summary>
    private string GraphqlBase(PrUrlRef target) =>
        _isEnterprise ? $"https://{target.Host}/api/graphql" : "https://api.github.com/graphql";

    public async Task<ThreadResolveResult> ResolveThreadsAsync(
        ThreadResolveRequest request, CancellationToken ct)
    {
        if (request.ThreadNodeIds.Count == 0)
        {
            return ThreadResolveResult.Failure(_identityUsed, "No thread ids supplied.");
        }

        // Dedupe defensively. Orchestrator should already have done this,
        // but a single thread sharing N comments easily yields N copies.
        var ids = request.ThreadNodeIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0)
        {
            return ThreadResolveResult.Failure(_identityUsed, "No non-empty thread ids supplied.");
        }

        PrUrlRef target;
        try { target = PullRequestUrlParser.Parse(request.PrUrl); }
        catch (ArgumentException ex)
        {
            return ThreadResolveResult.Failure(_identityUsed, ex.Message);
        }

        if (target.Kind == PrPlatform.AzureDevOps)
        {
            return ThreadResolveResult.Failure(_identityUsed,
                $"This publisher handles GitHub, not Azure DevOps URLs ({request.PrUrl}).");
        }

        if (request.DryRun)
        {
            return ThreadResolveResult.DryRunPlan(
                wouldResolve: ids,
                identityUsed: _identityUsed,
                warning: $"Dry-run: would resolve {ids.Count} review thread(s) on {target.Owner}/{target.Repo}#{target.Number} via {GraphqlBase(target)} as '{_identityUsed}'.");
        }

        string token;
        try { token = await _tokens.GetTokenAsync(ct); }
        catch (Exception ex)
        {
            return ThreadResolveResult.Failure(_identityUsed, $"Token acquisition failed: {ex.Message}");
        }

        var resolved = new List<string>();
        var alreadyResolved = new List<string>();
        var failed = new List<string>();
        var warnings = new List<string>();
        var errors = new List<string>();

        foreach (var threadId in ids)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await ResolveOneAsync(token, target, threadId, ct);
            switch (outcome.Status)
            {
                case ResolveStatus.Resolved:
                    resolved.Add(threadId);
                    break;
                case ResolveStatus.AlreadyResolved:
                    alreadyResolved.Add(threadId);
                    break;
                default:
                    failed.Add(threadId);
                    errors.Add($"{threadId}: {outcome.Error}");
                    break;
            }
        }

        return new ThreadResolveResult(
            Performed: true,
            ResolvedNodeIds: resolved,
            AlreadyResolvedNodeIds: alreadyResolved,
            FailedNodeIds: failed,
            IdentityUsed: _identityUsed,
            Warnings: warnings,
            Errors: errors);
    }

    private enum ResolveStatus { Resolved, AlreadyResolved, Failed }
    private readonly record struct ResolveOutcome(ResolveStatus Status, string? Error);

    private const string ResolveThreadMutation = """
        mutation($threadId: ID!) {
          resolveReviewThread(input: { threadId: $threadId }) {
            thread { id isResolved }
          }
        }
        """;

    private async Task<ResolveOutcome> ResolveOneAsync(
        string token, PrUrlRef target, string threadId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, GraphqlBase(target));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("pr-inbox", "0.2"));
        req.Content = JsonContent.Create(new
        {
            query = ResolveThreadMutation,
            variables = new { threadId },
        });

        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, ct); }
        catch (Exception ex)
        {
            return new ResolveOutcome(ResolveStatus.Failed, $"HTTP send failed: {ex.Message}");
        }

        var respText = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var hint = resp.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Authentication failed.",
                HttpStatusCode.Forbidden => "Forbidden. This identity may lack write access.",
                HttpStatusCode.NotFound => "Thread not found. The node id may be stale.",
                _ => null,
            };
            var msg = hint is null
                ? $"GraphQL returned {(int)resp.StatusCode}: {respText}"
                : $"GraphQL returned {(int)resp.StatusCode} ({hint}): {respText}";
            _log.LogWarning("GitHub resolve failed for {Thread}: {Msg}", threadId, msg);
            return new ResolveOutcome(ResolveStatus.Failed, msg);
        }

        // GraphQL returns 200 OK even on user-facing errors. Inspect body.
        JsonDocument doc;
        try { doc = JsonDocument.Parse(respText); }
        catch (JsonException ex)
        {
            return new ResolveOutcome(ResolveStatus.Failed, $"Could not parse GraphQL response: {ex.Message}");
        }
        using (doc)
        {
            if (doc.RootElement.TryGetProperty("errors", out var errs)
                && errs.ValueKind == JsonValueKind.Array
                && errs.GetArrayLength() > 0)
            {
                // Best-effort detection of "already resolved" so the user
                // doesn't see a spurious error when two reviewers race.
                // GitHub's exact code string for this case is documented as
                // an unprocessable / validation error; we conservatively
                // pattern-match on the message to avoid coupling to a
                // specific code string that may change.
                foreach (var err in errs.EnumerateArray())
                {
                    var msg = err.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                    if (msg.Contains("already resolved", StringComparison.OrdinalIgnoreCase) ||
                        msg.Contains("Thread is resolved", StringComparison.OrdinalIgnoreCase))
                    {
                        return new ResolveOutcome(ResolveStatus.AlreadyResolved, null);
                    }
                }
                return new ResolveOutcome(ResolveStatus.Failed, $"GraphQL errors: {errs}");
            }

            // Success path: check the returned isResolved flag. If true,
            // count it as Resolved (we don't distinguish "we just resolved"
            // from "it was already resolved upstream" when the mutation
            // returned success — both are wins).
            if (doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("resolveReviewThread", out var rrt)
                && rrt.TryGetProperty("thread", out var th)
                && th.TryGetProperty("isResolved", out var ir)
                && ir.ValueKind == JsonValueKind.True)
            {
                return new ResolveOutcome(ResolveStatus.Resolved, null);
            }

            return new ResolveOutcome(ResolveStatus.Failed,
                "GraphQL returned 200 OK but the thread did not transition to resolved.");
        }
    }

    private async Task<string?> TryGetCurrentHeadAsync(string token, PrUrlRef target, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Get,
                $"{ApiBase(target)}/repos/{target.Owner}/{target.Repo}/pulls/{target.Number}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("pr-inbox", "0.1"));
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts, ct);
            if (doc.TryGetProperty("head", out var head) && head.TryGetProperty("sha", out var sha))
            {
                return sha.GetString();
            }
        }
        catch
        {
            // Best-effort; missing head check is a soft warning, not a hard error.
        }
        return null;
    }

    private static string MapEvent(ReviewEvent e) => e switch
    {
        ReviewEvent.Approve => "APPROVE",
        ReviewEvent.RequestChanges => "REQUEST_CHANGES",
        _ => "COMMENT",
    };

    private static InlineCommentPayload BuildInlinePayload(FindingToPost f)
    {
        var payload = new InlineCommentPayload
        {
            Path = f.File,
            Body = PublishHelpers.ComposeInlineCommentBody(f),
            Side = "RIGHT",
            Line = f.LineEnd ?? f.Line,
        };
        if (f.LineEnd is int end && end > f.Line)
        {
            payload.StartLine = f.Line;
            payload.StartSide = "RIGHT";
        }
        return payload;
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class ReviewBody
    {
        [JsonPropertyName("commit_id")] public string CommitId { get; set; } = "";
        [JsonPropertyName("event")] public string Event { get; set; } = "COMMENT";
        [JsonPropertyName("body")] public string Body { get; set; } = "";
        [JsonPropertyName("comments")] public InlineCommentPayload[] Comments { get; set; } = Array.Empty<InlineCommentPayload>();
    }

    private sealed class InlineCommentPayload
    {
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("body")] public string Body { get; set; } = "";
        [JsonPropertyName("line")] public int Line { get; set; }
        [JsonPropertyName("side")] public string Side { get; set; } = "RIGHT";
        [JsonPropertyName("start_line")] public int? StartLine { get; set; }
        [JsonPropertyName("start_side")] public string? StartSide { get; set; }
    }

    private sealed class ReviewResponse
    {
        // GitHub returns both numeric "id" and string "node_id"; we keep
        // the numeric one as a string for storage uniformity.
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("node_id")] public string? NodeId { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        public string IdString => Id != 0 ? Id.ToString() : (NodeId ?? "");
    }
}
