using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PrInbox.Core.Credentials;
using PrInbox.Core.Findings;
using Xunit;

namespace PrInbox.Publishers.Tests;

public sealed class GitHubReviewPublisherTests
{
    [Fact]
    public async Task DryRun_makes_zero_http_requests()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException(
            "DryRun must not make any HTTP call."));
        using var http = new HttpClient(handler);
        var token = new FakeTokenProvider(_ => throw new InvalidOperationException(
            "DryRun must not even fetch a token."));

        var publisher = new GitHubReviewPublisher(
            token, http, isEnterprise: false, host: "github.com",
            identityUsed: "jmprieur",
            log: NullLogger<GitHubReviewPublisher>.Instance);

        var req = MakeRequest(
            url: "https://github.com/owner/repo/pull/42",
            findings: new[] { Finding("f01", anchorable: true) },
            dryRun: true);

        var result = await publisher.PublishAsync(req, CancellationToken.None);

        result.Posted.Should().BeFalse();
        result.InlineCount.Should().Be(1);
        result.Errors.Should().BeEmpty();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task DryRun_with_ValidateRemoteState_still_makes_zero_requests()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException(
            "DryRun must not even consult remote state."));
        using var http = new HttpClient(handler);
        var token = new FakeTokenProvider(_ => "tk");

        var publisher = new GitHubReviewPublisher(
            token, http, isEnterprise: false, host: "github.com",
            identityUsed: "jmprieur",
            log: NullLogger<GitHubReviewPublisher>.Instance);

        var req = MakeRequest(
            url: "https://github.com/owner/repo/pull/42",
            findings: new[] { Finding("f01") },
            dryRun: true,
            validateRemoteState: true);                             // ⚠ should be ignored

        var result = await publisher.PublishAsync(req, CancellationToken.None);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Live_posts_review_to_correct_endpoint_with_correct_shape()
    {
        var handler = new RecordingHandler(req =>
        {
            req.Method.Should().Be(HttpMethod.Post);
            req.RequestUri!.ToString().Should().Be(
                "https://api.github.com/repos/owner/repo/pulls/42/reviews");
            req.Headers.Authorization!.Scheme.Should().Be("Bearer");
            req.Headers.Authorization.Parameter.Should().Be("tk");
            req.Headers.UserAgent.ToString().Should().StartWith("pr-inbox/");

            var payload = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
            payload.RootElement.GetProperty("commit_id").GetString().Should().Be("abc123");
            payload.RootElement.GetProperty("event").GetString().Should().Be("COMMENT");
            var comments = payload.RootElement.GetProperty("comments");
            comments.GetArrayLength().Should().Be(1);
            comments[0].GetProperty("path").GetString().Should().Be("src/Foo.cs");
            comments[0].GetProperty("line").GetInt32().Should().Be(42);
            comments[0].GetProperty("side").GetString().Should().Be("RIGHT");

            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(@"{""id"":12345,""html_url"":""https://github.com/owner/repo/pull/42#pullrequestreview-12345""}",
                    System.Text.Encoding.UTF8, "application/json"),
            };
        });
        using var http = new HttpClient(handler);
        var token = new FakeTokenProvider(_ => "tk");

        var publisher = new GitHubReviewPublisher(
            token, http, isEnterprise: false, host: "github.com",
            identityUsed: "jmprieur",
            log: NullLogger<GitHubReviewPublisher>.Instance);

        var req = MakeRequest(
            url: "https://github.com/owner/repo/pull/42",
            findings: new[] { Finding("f01", anchorable: true) },
            dryRun: false,
            headSha: "abc123");

        var result = await publisher.PublishAsync(req, CancellationToken.None);

        result.Posted.Should().BeTrue();
        result.PlatformReviewId.Should().Be("12345");
        result.ReviewUrl.Should().Contain("pullrequestreview-12345");
        result.InlineCount.Should().Be(1);
        handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task GHE_uses_api_v3_base()
    {
        var handler = new RecordingHandler(req =>
        {
            req.RequestUri!.ToString().Should().Be(
                "https://ghe.example.com/api/v3/repos/octocat/hello-world/pulls/585/reviews");
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(@"{""id"":1,""html_url"":""url""}", System.Text.Encoding.UTF8, "application/json"),
            };
        });
        using var http = new HttpClient(handler);
        var token = new FakeTokenProvider(_ => "tk");

        var publisher = new GitHubReviewPublisher(
            token, http, isEnterprise: true, host: "ghe.example.com",
            identityUsed: "jean-marc-prieur",
            log: NullLogger<GitHubReviewPublisher>.Instance);

        var req = MakeRequest(
            url: "https://ghe.example.com/octocat/hello-world/pull/585",
            findings: new[] { Finding("f01") });

        var result = await publisher.PublishAsync(req, CancellationToken.None);
        result.Posted.Should().BeTrue();
    }

    [Fact]
    public async Task Unauthorized_surfaces_friendly_error()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("Bad credentials"),
        });
        using var http = new HttpClient(handler);
        var token = new FakeTokenProvider(_ => "tk");

        var publisher = new GitHubReviewPublisher(
            token, http, isEnterprise: false, host: "github.com",
            identityUsed: "jmprieur",
            log: NullLogger<GitHubReviewPublisher>.Instance);

        var req = MakeRequest(url: "https://github.com/owner/repo/pull/42",
            findings: new[] { Finding("f01") });

        var result = await publisher.PublishAsync(req, CancellationToken.None);
        result.Posted.Should().BeFalse();
        result.Errors.Should().HaveCount(1);
        result.Errors[0].Should().Contain("Authentication failed");
    }

    [Fact]
    public async Task Mixed_anchorable_and_non_anchorable_findings_packages_correctly()
    {
        var handler = new RecordingHandler(req =>
        {
            var payload = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
            payload.RootElement.GetProperty("comments").GetArrayLength().Should().Be(1);
            payload.RootElement.GetProperty("body").GetString()!.Should().Contain("Non-anchorable findings");
            payload.RootElement.GetProperty("body").GetString()!.Should().Contain("src/B.cs:7");
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(@"{""id"":1,""html_url"":""url""}", System.Text.Encoding.UTF8, "application/json"),
            };
        });
        using var http = new HttpClient(handler);
        var token = new FakeTokenProvider(_ => "tk");

        var publisher = new GitHubReviewPublisher(
            token, http, isEnterprise: false, host: "github.com",
            identityUsed: "jmprieur",
            log: NullLogger<GitHubReviewPublisher>.Instance);

        var req = MakeRequest(
            url: "https://github.com/owner/repo/pull/42",
            findings: new[]
            {
                Finding("f01", file: "src/A.cs", anchorable: true),
                Finding("f02", file: "src/B.cs", line: 7, anchorable: false),
            });

        var result = await publisher.PublishAsync(req, CancellationToken.None);
        result.Posted.Should().BeTrue();
        result.InlineCount.Should().Be(1);
        result.BodyOnlyCount.Should().Be(1);
    }

    [Fact]
    public async Task Approve_event_sends_APPROVE_in_payload()
    {
        var handler = new RecordingHandler(req =>
        {
            var payload = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
            payload.RootElement.GetProperty("event").GetString().Should().Be("APPROVE");
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(@"{""id"":1,""html_url"":""u""}", System.Text.Encoding.UTF8, "application/json"),
            };
        });
        using var http = new HttpClient(handler);
        var token = new FakeTokenProvider(_ => "tk");

        var publisher = new GitHubReviewPublisher(
            token, http, isEnterprise: false, host: "github.com",
            identityUsed: "jmprieur",
            log: NullLogger<GitHubReviewPublisher>.Instance);

        var req = MakeRequest(
            url: "https://github.com/owner/repo/pull/42",
            findings: new[] { Finding("f01") },
            reviewEvent: ReviewEvent.Approve);

        var result = await publisher.PublishAsync(req, CancellationToken.None);
        result.Posted.Should().BeTrue();
    }

    [Fact]
    public async Task RequestChanges_event_sends_REQUEST_CHANGES_in_payload()
    {
        var handler = new RecordingHandler(req =>
        {
            var payload = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
            payload.RootElement.GetProperty("event").GetString().Should().Be("REQUEST_CHANGES");
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(@"{""id"":1,""html_url"":""u""}", System.Text.Encoding.UTF8, "application/json"),
            };
        });
        using var http = new HttpClient(handler);
        var token = new FakeTokenProvider(_ => "tk");

        var publisher = new GitHubReviewPublisher(
            token, http, isEnterprise: false, host: "github.com",
            identityUsed: "jmprieur",
            log: NullLogger<GitHubReviewPublisher>.Instance);

        var req = MakeRequest(
            url: "https://github.com/owner/repo/pull/42",
            findings: new[] { Finding("f01") },
            reviewEvent: ReviewEvent.RequestChanges);

        var result = await publisher.PublishAsync(req, CancellationToken.None);
        result.Posted.Should().BeTrue();
    }

    [Fact]
    public async Task Approve_with_zero_findings_is_allowed()
    {
        var handler = new RecordingHandler(req =>
        {
            var payload = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
            payload.RootElement.GetProperty("event").GetString().Should().Be("APPROVE");
            payload.RootElement.GetProperty("comments").GetArrayLength().Should().Be(0);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(@"{""id"":1,""html_url"":""u""}", System.Text.Encoding.UTF8, "application/json"),
            };
        });
        using var http = new HttpClient(handler);
        var token = new FakeTokenProvider(_ => "tk");

        var publisher = new GitHubReviewPublisher(
            token, http, isEnterprise: false, host: "github.com",
            identityUsed: "jmprieur",
            log: NullLogger<GitHubReviewPublisher>.Instance);

        var req = MakeRequest(
            url: "https://github.com/owner/repo/pull/42",
            findings: Array.Empty<FindingToPost>(),
            reviewEvent: ReviewEvent.Approve);

        var result = await publisher.PublishAsync(req, CancellationToken.None);
        result.Posted.Should().BeTrue();
        result.InlineCount.Should().Be(0);
        result.BodyOnlyCount.Should().Be(0);
    }

    [Fact]
    public async Task Comment_with_zero_findings_is_rejected()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException(
            "Should fail before any HTTP call."));
        using var http = new HttpClient(handler);
        var token = new FakeTokenProvider(_ => "tk");

        var publisher = new GitHubReviewPublisher(
            token, http, isEnterprise: false, host: "github.com",
            identityUsed: "jmprieur",
            log: NullLogger<GitHubReviewPublisher>.Instance);

        var req = MakeRequest(
            url: "https://github.com/owner/repo/pull/42",
            findings: Array.Empty<FindingToPost>(),
            reviewEvent: ReviewEvent.Comment);

        var result = await publisher.PublishAsync(req, CancellationToken.None);
        result.Posted.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("No findings selected"));
        handler.Requests.Should().BeEmpty();
    }

    // -- ResolveThreadsAsync ----------------------------------------------

    [Fact]
    public async Task ResolveThreads_DryRun_makes_zero_http_requests()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException(
            "DryRun must not make any HTTP call."));
        using var http = new HttpClient(handler);
        var token = new FakeTokenProvider(_ => throw new InvalidOperationException(
            "DryRun must not even fetch a token."));

        var publisher = new GitHubReviewPublisher(
            token, http, isEnterprise: false, host: "github.com",
            identityUsed: "jmprieur",
            log: NullLogger<GitHubReviewPublisher>.Instance);

        var result = await publisher.ResolveThreadsAsync(
            new ThreadResolveRequest(
                PrUrl: "https://github.com/owner/repo/pull/42",
                ThreadNodeIds: new[] { "PRRT_one", "PRRT_two" },
                DryRun: true),
            CancellationToken.None);

        result.Performed.Should().BeFalse();
        result.ResolvedNodeIds.Should().BeEquivalentTo(new[] { "PRRT_one", "PRRT_two" });
        result.Errors.Should().BeEmpty();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveThreads_dedupes_thread_ids_before_mutating()
    {
        var seen = new List<string>();
        var handler = new RecordingHandler(req =>
        {
            var body = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
            seen.Add(body.RootElement.GetProperty("variables").GetProperty("threadId").GetString()!);
            return ResolveOkResponse();
        });
        using var http = new HttpClient(handler);
        var token = new FakeTokenProvider(_ => "tk");

        var publisher = new GitHubReviewPublisher(
            token, http, isEnterprise: false, host: "github.com",
            identityUsed: "jmprieur",
            log: NullLogger<GitHubReviewPublisher>.Instance);

        var result = await publisher.ResolveThreadsAsync(
            new ThreadResolveRequest(
                PrUrl: "https://github.com/owner/repo/pull/42",
                ThreadNodeIds: new[] { "PRRT_one", "PRRT_one", "PRRT_two", "" },
                DryRun: false),
            CancellationToken.None);

        result.Performed.Should().BeTrue();
        seen.Should().BeEquivalentTo(new[] { "PRRT_one", "PRRT_two" });
        result.ResolvedNodeIds.Should().BeEquivalentTo(new[] { "PRRT_one", "PRRT_two" });
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveThreads_targets_graphql_endpoint_with_bearer_auth()
    {
        var handler = new RecordingHandler(req =>
        {
            req.Method.Should().Be(HttpMethod.Post);
            req.RequestUri!.ToString().Should().Be("https://api.github.com/graphql");
            req.Headers.Authorization!.Scheme.Should().Be("Bearer");
            req.Headers.Authorization.Parameter.Should().Be("tk");
            var payload = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
            payload.RootElement.GetProperty("query").GetString()!
                .Should().Contain("resolveReviewThread");
            payload.RootElement.GetProperty("variables").GetProperty("threadId").GetString()
                .Should().Be("PRRT_one");
            return ResolveOkResponse();
        });
        using var http = new HttpClient(handler);
        var token = new FakeTokenProvider(_ => "tk");

        var publisher = new GitHubReviewPublisher(
            token, http, isEnterprise: false, host: "github.com",
            identityUsed: "jmprieur",
            log: NullLogger<GitHubReviewPublisher>.Instance);

        var result = await publisher.ResolveThreadsAsync(
            new ThreadResolveRequest(
                PrUrl: "https://github.com/owner/repo/pull/42",
                ThreadNodeIds: new[] { "PRRT_one" },
                DryRun: false),
            CancellationToken.None);

        result.Performed.Should().BeTrue();
        result.ResolvedNodeIds.Should().ContainSingle().Which.Should().Be("PRRT_one");
    }

    [Fact]
    public async Task ResolveThreads_ghe_uses_api_graphql_endpoint_not_v3()
    {
        var observed = "";
        var handler = new RecordingHandler(req =>
        {
            observed = req.RequestUri!.ToString();
            return ResolveOkResponse();
        });
        using var http = new HttpClient(handler);
        var token = new FakeTokenProvider(_ => "tk");

        var publisher = new GitHubReviewPublisher(
            token, http, isEnterprise: true, host: "ghe.example.com",
            identityUsed: "jmprieur_microsoft",
            log: NullLogger<GitHubReviewPublisher>.Instance);

        await publisher.ResolveThreadsAsync(
            new ThreadResolveRequest(
                PrUrl: "https://ghe.example.com/owner/repo/pull/42",
                ThreadNodeIds: new[] { "PRRT_one" },
                DryRun: false),
            CancellationToken.None);

        observed.Should().Be("https://ghe.example.com/api/graphql");
    }

    [Fact]
    public async Task ResolveThreads_treats_already_resolved_GraphQL_error_as_success_bucket()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{ \"errors\": [ { \"message\": \"Thread is already resolved.\", \"type\": \"UNPROCESSABLE\" } ] }",
                System.Text.Encoding.UTF8, "application/json"),
        });
        using var http = new HttpClient(handler);
        var token = new FakeTokenProvider(_ => "tk");

        var publisher = new GitHubReviewPublisher(
            token, http, isEnterprise: false, host: "github.com",
            identityUsed: "jmprieur",
            log: NullLogger<GitHubReviewPublisher>.Instance);

        var result = await publisher.ResolveThreadsAsync(
            new ThreadResolveRequest(
                PrUrl: "https://github.com/owner/repo/pull/42",
                ThreadNodeIds: new[] { "PRRT_one" },
                DryRun: false),
            CancellationToken.None);

        result.Performed.Should().BeTrue();
        result.ResolvedNodeIds.Should().BeEmpty();
        result.AlreadyResolvedNodeIds.Should().ContainSingle().Which.Should().Be("PRRT_one");
        result.FailedNodeIds.Should().BeEmpty();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveThreads_partitions_per_thread_outcomes_when_some_succeed_and_some_fail()
    {
        var handler = new RecordingHandler(req =>
        {
            var body = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
            var id = body.RootElement.GetProperty("variables").GetProperty("threadId").GetString();
            return id switch
            {
                "PRRT_ok" => ResolveOkResponse(),
                "PRRT_already" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"errors\":[{\"message\":\"Thread is already resolved.\"}]}",
                        System.Text.Encoding.UTF8, "application/json"),
                },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("{\"message\":\"Not Found\"}",
                        System.Text.Encoding.UTF8, "application/json"),
                },
            };
        });
        using var http = new HttpClient(handler);
        var token = new FakeTokenProvider(_ => "tk");

        var publisher = new GitHubReviewPublisher(
            token, http, isEnterprise: false, host: "github.com",
            identityUsed: "jmprieur",
            log: NullLogger<GitHubReviewPublisher>.Instance);

        var result = await publisher.ResolveThreadsAsync(
            new ThreadResolveRequest(
                PrUrl: "https://github.com/owner/repo/pull/42",
                ThreadNodeIds: new[] { "PRRT_ok", "PRRT_already", "PRRT_missing" },
                DryRun: false),
            CancellationToken.None);

        result.Performed.Should().BeTrue();
        result.ResolvedNodeIds.Should().ContainSingle().Which.Should().Be("PRRT_ok");
        result.AlreadyResolvedNodeIds.Should().ContainSingle().Which.Should().Be("PRRT_already");
        result.FailedNodeIds.Should().ContainSingle().Which.Should().Be("PRRT_missing");
        result.Errors.Should().ContainSingle().Which.Should().Contain("PRRT_missing");
    }

    [Fact]
    public async Task ResolveThreads_returns_failure_when_no_ids_supplied()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException(
            "Empty selection must not call out."));
        using var http = new HttpClient(handler);
        var token = new FakeTokenProvider(_ => "tk");

        var publisher = new GitHubReviewPublisher(
            token, http, isEnterprise: false, host: "github.com",
            identityUsed: "jmprieur",
            log: NullLogger<GitHubReviewPublisher>.Instance);

        var result = await publisher.ResolveThreadsAsync(
            new ThreadResolveRequest(
                PrUrl: "https://github.com/owner/repo/pull/42",
                ThreadNodeIds: Array.Empty<string>(),
                DryRun: false),
            CancellationToken.None);

        result.Performed.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveThreads_rejects_ADO_url()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException(
            "ADO URL must not call GraphQL."));
        using var http = new HttpClient(handler);
        var token = new FakeTokenProvider(_ => "tk");

        var publisher = new GitHubReviewPublisher(
            token, http, isEnterprise: false, host: "github.com",
            identityUsed: "jmprieur",
            log: NullLogger<GitHubReviewPublisher>.Instance);

        var result = await publisher.ResolveThreadsAsync(
            new ThreadResolveRequest(
                PrUrl: "https://dev.azure.com/o/p/_git/r/pullrequest/1",
                ThreadNodeIds: new[] { "PRRT_x" },
                DryRun: false),
            CancellationToken.None);

        result.Performed.Should().BeFalse();
        result.Errors.Should().ContainMatch("*GitHub*Azure DevOps*");
    }

    private static HttpResponseMessage ResolveOkResponse() =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{ \"data\": { \"resolveReviewThread\": { \"thread\": { \"id\": \"PRRT_x\", \"isResolved\": true } } } }",
                System.Text.Encoding.UTF8, "application/json"),
        };

    // -- helpers --

    private static PublishRequest MakeRequest(
        string url,
        IReadOnlyList<FindingToPost> findings,
        bool dryRun = false,
        bool validateRemoteState = false,
        string headSha = "abc123",
        ReviewEvent reviewEvent = ReviewEvent.Comment) =>
        new(
            PrUrl: url,
            RunId: 7,
            HeadShaAtAuthoring: headSha,
            ReviewBodyHeader: "Findings: 1 (1 critical, 0 high, 0 medium, 0 low).",
            Findings: findings,
            DryRun: dryRun,
            ValidateRemoteState: validateRemoteState,
            Event: reviewEvent);

    private static FindingToPost Finding(
        string id, string file = "src/Foo.cs", int line = 42, bool anchorable = true)
        => new(
            Id: id,
            Severity: FindingSeverity.Critical,
            Confidence: FindingConfidence.High,
            FoundBy: new[] { "opus", "gpt" },
            File: file,
            Line: line,
            LineEnd: null,
            DiffAnchorable: anchorable,
            Title: $"Issue {id}",
            Body: $"Body for {id}.",
            SuggestedInline: null);
}

internal sealed class RecordingHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _factory;
    public List<HttpRequestMessage> Requests { get; } = new();

    public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> factory)
    {
        _factory = factory;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(_factory(request));
    }
}

internal sealed class FakeTokenProvider : ITokenProvider
{
    private readonly Func<CancellationToken, string> _tokenFactory;
    public FakeTokenProvider(Func<CancellationToken, string> tokenFactory) => _tokenFactory = tokenFactory;
    public string SourceId => "fake";
    public Task<string> GetTokenAsync(CancellationToken ct = default) => Task.FromResult(_tokenFactory(ct));
    public Task<string?> GetAuthenticatedIdentityAsync(CancellationToken ct = default) =>
        Task.FromResult<string?>("fake");
}
