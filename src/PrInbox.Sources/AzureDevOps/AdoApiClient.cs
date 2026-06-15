using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PrInbox.Core.Credentials;

namespace PrInbox.Sources.AzureDevOps;

/// <summary>
/// Thin HTTP wrapper around the Azure DevOps REST APIs the read source needs.
/// Owns auth-header attachment via <see cref="ITokenProvider"/>, retry on
/// transient failures, and JSON deserialization. No business logic.
/// </summary>
internal sealed class AdoApiClient
{
    private const string ApiVersion = "7.1";

    private readonly string _org;
    private readonly ITokenProvider _tokenProvider;
    private readonly HttpClient _http;
    private readonly ILogger<AdoApiClient> _logger;

    public AdoApiClient(string org, ITokenProvider tokenProvider, HttpClient? http = null, ILogger<AdoApiClient>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(org);
        _org = org;
        _tokenProvider = tokenProvider;
        _http = http ?? new HttpClient();
        _logger = logger ?? NullLogger<AdoApiClient>.Instance;
    }

    /// <summary>
    /// Resolve the authenticated user's VSTS profile id. Required as the
    /// <c>searchCriteria.reviewerId</c> for the PR-list query.
    /// </summary>
    public async Task<AdoDtos.ProfileResponse> GetMyProfileAsync(CancellationToken ct)
    {
        // Profile API lives under vssps.dev.azure.com, *not* the org host.
        var url = $"https://vssps.dev.azure.com/_apis/profile/profiles/me?api-version={ApiVersion}";
        return await GetJsonAsync<AdoDtos.ProfileResponse>(url, ct);
    }

    /// <summary>
    /// List active PRs in <paramref name="project"/> where the authenticated
    /// user is a reviewer. Pages internally via <c>$skip</c>; returns when
    /// a page returns fewer than <paramref name="pageSize"/> rows.
    /// </summary>
    public IAsyncEnumerable<AdoDtos.PullRequest> ListPullRequestsForReviewerAsync(
        string project,
        string reviewerId,
        int pageSize = 100,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewerId);
        return ListPullRequestsByCriterionAsync(project, "reviewerId", reviewerId, pageSize, ct);
    }

    /// <summary>
    /// List active PRs in <paramref name="project"/> that the authenticated
    /// user <em>created</em> (authored). Same paging as the reviewer query,
    /// keyed on <c>searchCriteria.creatorId</c>.
    /// </summary>
    public IAsyncEnumerable<AdoDtos.PullRequest> ListPullRequestsForCreatorAsync(
        string project,
        string creatorId,
        int pageSize = 100,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(creatorId);
        return ListPullRequestsByCriterionAsync(project, "creatorId", creatorId, pageSize, ct);
    }

    private async IAsyncEnumerable<AdoDtos.PullRequest> ListPullRequestsByCriterionAsync(
        string project,
        string criterion,
        string value,
        int pageSize,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);

        var skip = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var url = $"https://dev.azure.com/{Uri.EscapeDataString(_org)}/{Uri.EscapeDataString(project)}/_apis/git/pullrequests" +
                      $"?searchCriteria.{criterion}={Uri.EscapeDataString(value)}" +
                      "&searchCriteria.status=active" +
                      $"&$top={pageSize}&$skip={skip}" +
                      $"&api-version={ApiVersion}";

            var page = await GetJsonAsync<AdoDtos.ListResponse<AdoDtos.PullRequest>>(url, ct);
            foreach (var pr in page.Value)
            {
                yield return pr;
            }

            if (page.Value.Count < pageSize)
            {
                yield break;
            }
            skip += pageSize;
            if (skip > 5000)
            {
                _logger.LogWarning("ADO list reached safety cap at skip={Skip} for project {Project}", skip, project);
                yield break;
            }
        }
    }

    public async Task<AdoDtos.PullRequest> GetPullRequestAsync(string project, string repoId, int prId, CancellationToken ct)
    {
        var url = $"https://dev.azure.com/{Uri.EscapeDataString(_org)}/{Uri.EscapeDataString(project)}/_apis/git/repositories/{Uri.EscapeDataString(repoId)}/pullrequests/{prId}" +
                  $"?api-version={ApiVersion}";
        return await GetJsonAsync<AdoDtos.PullRequest>(url, ct);
    }

    public async Task<IReadOnlyList<AdoDtos.Thread>> GetThreadsAsync(string project, string repoId, int prId, CancellationToken ct)
    {
        var url = $"https://dev.azure.com/{Uri.EscapeDataString(_org)}/{Uri.EscapeDataString(project)}/_apis/git/repositories/{Uri.EscapeDataString(repoId)}/pullrequests/{prId}/threads" +
                  $"?api-version={ApiVersion}";
        var page = await GetJsonAsync<AdoDtos.ListResponse<AdoDtos.Thread>>(url, ct);
        return page.Value;
    }

    public async Task<IReadOnlyList<AdoDtos.Commit>> GetCommitsAsync(string project, string repoId, int prId, CancellationToken ct)
    {
        var url = $"https://dev.azure.com/{Uri.EscapeDataString(_org)}/{Uri.EscapeDataString(project)}/_apis/git/repositories/{Uri.EscapeDataString(repoId)}/pullrequests/{prId}/commits" +
                  $"?api-version={ApiVersion}";
        var page = await GetJsonAsync<AdoDtos.ListResponse<AdoDtos.Commit>>(url, ct);
        return page.Value;
    }

    private async Task<T> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        var token = await _tokenProvider.GetTokenAsync(ct);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException ex) when (attempt < 2)
            {
                _logger.LogWarning(ex, "ADO request transient failure (attempt {Attempt}); retrying.", attempt + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), ct);
                continue;
            }

            using (response)
            {
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable && attempt < 2)
                {
                    var delay = TimeSpan.FromSeconds(1 << attempt);
                    _logger.LogWarning("ADO returned {Status} for {Url}; backing off {Delay}s.", response.StatusCode, url, delay.TotalSeconds);
                    await Task.Delay(delay, ct);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    throw new AdoApiException($"ADO GET {url} -> {(int)response.StatusCode}: {Truncate(body, 500)}", response.StatusCode);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var dto = await JsonSerializer.DeserializeAsync<T>(stream, SerializerOptions, ct)
                          ?? throw new AdoApiException($"ADO GET {url}: empty response body.", HttpStatusCode.OK);
                return dto;
            }
        }

        throw new AdoApiException($"ADO GET {url}: exhausted retries.", HttpStatusCode.ServiceUnavailable);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "(empty)" : (s.Length <= max ? s : s[..max] + "...");
}

internal sealed class AdoApiException : Exception
{
    public AdoApiException(string message, HttpStatusCode statusCode) : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode StatusCode { get; }
}
