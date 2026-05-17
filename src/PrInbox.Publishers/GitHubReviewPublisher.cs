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
    /// <param name="host">e.g. <c>github.com</c> or <c>microsoft.ghe.com</c>.</param>
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
