using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PrInbox.Core.Credentials;

namespace PrInbox.Publishers;

/// <summary>
/// Posts review findings to Azure DevOps as pull-request threads. Inline
/// findings get a <c>threadContext</c> targeting the right-file range;
/// non-anchorable findings post as PR-level threads.
/// </summary>
/// <remarks>
/// ADO does not bundle multiple inline comments into a single review the
/// way GitHub does. Each finding becomes its own thread. We coalesce the
/// review header into a separate PR-level thread that links the others.
/// </remarks>
public sealed class AdoReviewPublisher : IPrReviewPublisher
{
    private const string ApiVersion = "7.1";

    private readonly ITokenProvider _tokens;
    private readonly HttpClient _http;
    private readonly string _identityUsed;
    private readonly ILogger<AdoReviewPublisher> _log;

    public AdoReviewPublisher(
        ITokenProvider tokens,
        HttpClient http,
        string identityUsed,
        ILogger<AdoReviewPublisher> log)
    {
        _tokens = tokens;
        _http = http;
        _identityUsed = identityUsed;
        _log = log;
    }

    public string Kind => "azure-devops";

    public async Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken ct)
    {
        if (request.Event != ReviewEvent.Comment)
        {
            return PublishResult.Failure(_identityUsed,
                "Azure DevOps doesn't support an Approve / Request-changes vote through this publisher. " +
                "Use the PR page directly to vote.");
        }

        if (request.Findings.Count == 0)
        {
            return PublishResult.Failure(_identityUsed, "No findings selected.");
        }

        PrUrlRef target;
        try { target = PullRequestUrlParser.Parse(request.PrUrl); }
        catch (ArgumentException ex)
        {
            return PublishResult.Failure(_identityUsed, ex.Message);
        }

        if (target.Kind != PrPlatform.AzureDevOps)
        {
            return PublishResult.Failure(_identityUsed,
                $"This publisher handles Azure DevOps, not GitHub URLs ({request.PrUrl}).");
        }

        var anchorable = request.Findings.Where(f => f.DiffAnchorable).ToList();
        var nonAnchorable = request.Findings.Where(f => !f.DiffAnchorable).ToList();
        var reviewBody = PublishHelpers.ComposeReviewBody(
            request.ReviewBodyHeader, nonAnchorable, request.HeadShaAtAuthoring);

        // DRY RUN: no network at all.
        if (request.DryRun)
        {
            return PublishResult.DryRunPlan(
                inlineCount: anchorable.Count,
                bodyOnlyCount: nonAnchorable.Count + 1,  // +1 for the header thread
                skipped: 0,
                identityUsed: _identityUsed,
                warning: $"Dry-run: would create {anchorable.Count + nonAnchorable.Count + 1} thread(s) on {request.PrUrl} as '{_identityUsed}'.");
        }

        // Live path.
        string token;
        try { token = await _tokens.GetTokenAsync(ct); }
        catch (Exception ex)
        {
            return PublishResult.Failure(_identityUsed, $"Token acquisition failed: {ex.Message}");
        }

        // ADO threads endpoint needs a repoId, not a repo name. Resolve.
        var org = target.Owner;
        var project = target.AdoProject!;
        string repoId;
        try { repoId = await ResolveRepoIdAsync(token, org, project, target.Repo, ct); }
        catch (Exception ex)
        {
            return PublishResult.Failure(_identityUsed, $"Cannot resolve ADO repo id for '{target.Repo}': {ex.Message}");
        }

        // HEAD check is optional and uses one extra HTTP call.
        var headSha = request.HeadShaAtAuthoring;
        var headChanged = false;
        var warnings = new List<string>();
        if (request.ValidateRemoteState)
        {
            var fresh = await TryGetCurrentHeadAsync(token, org, project, repoId, target.Number, ct);
            if (fresh is not null && !fresh.Equals(request.HeadShaAtAuthoring, StringComparison.OrdinalIgnoreCase))
            {
                headSha = fresh;
                headChanged = true;
                warnings.Add($"HEAD changed since the brief was built: {PublishHelpers.ShortSha(request.HeadShaAtAuthoring)} -> {PublishHelpers.ShortSha(fresh)}.");
            }
        }

        // Post: 1 header thread + 1 per anchorable finding. Failures on
        // single threads don't abort the rest; we collect warnings and keep
        // going.
        var threadsUrl = $"https://dev.azure.com/{Uri.EscapeDataString(org)}/{Uri.EscapeDataString(project)}/_apis/git/repositories/{repoId}/pullRequests/{target.Number}/threads?api-version={ApiVersion}";

        // Header thread (covers the review body + any non-anchorables).
        var headerThreadResult = await PostThreadAsync(threadsUrl, token, new ThreadBody
        {
            Comments = new[] { new CommentBody { ParentCommentId = 0, Content = reviewBody, CommentType = 1 } },
            Status = 1,
            ThreadContext = null,
        }, ct);

        if (!headerThreadResult.Posted)
        {
            return PublishResult.Failure(_identityUsed,
                $"ADO header thread post failed: {string.Join("; ", headerThreadResult.Errors)}");
        }

        var inlineSuccess = 0;
        foreach (var f in anchorable)
        {
            var threadCtx = new ThreadContextBody
            {
                FilePath = f.File.StartsWith('/') ? f.File : "/" + f.File,
                RightFileStart = new FilePos { Line = f.Line, Offset = 1 },
                RightFileEnd = new FilePos { Line = f.LineEnd ?? f.Line, Offset = 1 },
            };
            var commentBody = PublishHelpers.ComposeInlineCommentBody(f);
            var threadResult = await PostThreadAsync(threadsUrl, token, new ThreadBody
            {
                Comments = new[] { new CommentBody { ParentCommentId = 0, Content = commentBody, CommentType = 1 } },
                Status = 1,
                ThreadContext = threadCtx,
            }, ct);

            if (threadResult.Posted) inlineSuccess++;
            else warnings.AddRange(threadResult.Errors.Select(e =>
                $"finding {f.Id} ({f.File}:{f.Line}) failed: {e}"));
        }

        if (warnings.Count > 0 && warnings.Any(w => w.StartsWith("finding ", StringComparison.Ordinal)))
        {
            warnings.Insert(0, $"{anchorable.Count - inlineSuccess} inline finding(s) failed to post; review body still landed.");
        }

        return new PublishResult(
            Posted: true,
            PlatformReviewId: headerThreadResult.ThreadId,
            ReviewUrl: $"{request.PrUrl}",
            InlineCount: inlineSuccess,
            BodyOnlyCount: nonAnchorable.Count + 1,
            SkippedAsAlreadyPosted: 0,
            HeadShaAtPost: headSha,
            HeadChanged: headChanged,
            IdentityUsed: _identityUsed,
            Warnings: warnings,
            Errors: Array.Empty<string>());
    }

    private async Task<(bool Posted, string ThreadId, IReadOnlyList<string> Errors)> PostThreadAsync(
        string url, string token, ThreadBody body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Content = JsonContent.Create(body, options: JsonOpts);

        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, ct); }
        catch (Exception ex) { return (false, "", new[] { $"HTTP send failed: {ex.Message}" }); }

        var respText = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var hint = resp.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Authentication failed.",
                HttpStatusCode.Forbidden => "This identity may lack permission to create threads.",
                HttpStatusCode.NotFound => "PR or repo not found.",
                HttpStatusCode.BadRequest => "Validation error from ADO (often invalid line range).",
                _ => null,
            };
            var err = hint is null
                ? $"ADO API returned {(int)resp.StatusCode}: {respText}"
                : $"ADO API returned {(int)resp.StatusCode} ({hint}): {respText}";
            _log.LogWarning("ADO thread post failed: {Err}", err);
            return (false, "", new[] { err });
        }

        try
        {
            var doc = JsonSerializer.Deserialize<ThreadResponse>(respText, JsonOpts);
            return (true, doc?.Id.ToString() ?? "", Array.Empty<string>());
        }
        catch (JsonException ex)
        {
            return (false, "", new[] { $"ADO API returned 2xx but body could not be parsed: {ex.Message}" });
        }
    }

    private async Task<string> ResolveRepoIdAsync(string token, string org, string project, string repoName, CancellationToken ct)
    {
        var url = $"https://dev.azure.com/{Uri.EscapeDataString(org)}/{Uri.EscapeDataString(project)}/_apis/git/repositories/{Uri.EscapeDataString(repoName)}?api-version={ApiVersion}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GET {url} -> {(int)resp.StatusCode}");
        }
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts, ct);
        if (doc.TryGetProperty("id", out var id) && id.GetString() is { } repoId)
        {
            return repoId;
        }
        throw new InvalidOperationException("ADO repo response missing 'id'.");
    }

    private async Task<string?> TryGetCurrentHeadAsync(string token, string org, string project, string repoId, int prId, CancellationToken ct)
    {
        try
        {
            var url = $"https://dev.azure.com/{Uri.EscapeDataString(org)}/{Uri.EscapeDataString(project)}/_apis/git/repositories/{repoId}/pullrequests/{prId}?api-version={ApiVersion}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts, ct);
            if (doc.TryGetProperty("lastMergeSourceCommit", out var commit) &&
                commit.TryGetProperty("commitId", out var sha))
            {
                return sha.GetString();
            }
        }
        catch { /* best-effort */ }
        return null;
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class ThreadBody
    {
        [JsonPropertyName("comments")] public CommentBody[] Comments { get; set; } = Array.Empty<CommentBody>();
        [JsonPropertyName("status")] public int Status { get; set; } = 1;
        [JsonPropertyName("threadContext")] public ThreadContextBody? ThreadContext { get; set; }
    }

    private sealed class CommentBody
    {
        [JsonPropertyName("parentCommentId")] public int ParentCommentId { get; set; }
        [JsonPropertyName("content")] public string Content { get; set; } = "";
        [JsonPropertyName("commentType")] public int CommentType { get; set; } = 1;
    }

    private sealed class ThreadContextBody
    {
        [JsonPropertyName("filePath")] public string FilePath { get; set; } = "";
        [JsonPropertyName("rightFileStart")] public FilePos? RightFileStart { get; set; }
        [JsonPropertyName("rightFileEnd")] public FilePos? RightFileEnd { get; set; }
    }

    private sealed class FilePos
    {
        [JsonPropertyName("line")] public int Line { get; set; }
        [JsonPropertyName("offset")] public int Offset { get; set; } = 1;
    }

    private sealed class ThreadResponse
    {
        [JsonPropertyName("id")] public long Id { get; set; }
    }
}
