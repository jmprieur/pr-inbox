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
                "https://microsoft.ghe.com/api/v3/repos/bic/IOM-Libs/pulls/585/reviews");
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(@"{""id"":1,""html_url"":""url""}", System.Text.Encoding.UTF8, "application/json"),
            };
        });
        using var http = new HttpClient(handler);
        var token = new FakeTokenProvider(_ => "tk");

        var publisher = new GitHubReviewPublisher(
            token, http, isEnterprise: true, host: "microsoft.ghe.com",
            identityUsed: "jean-marc-prieur",
            log: NullLogger<GitHubReviewPublisher>.Instance);

        var req = MakeRequest(
            url: "https://microsoft.ghe.com/bic/IOM-Libs/pull/585",
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

    // -- helpers --

    private static PublishRequest MakeRequest(
        string url,
        IReadOnlyList<FindingToPost> findings,
        bool dryRun = false,
        bool validateRemoteState = false,
        string headSha = "abc123") =>
        new(
            PrUrl: url,
            RunId: 7,
            HeadShaAtAuthoring: headSha,
            ReviewBodyHeader: "Findings: 1 (1 critical, 0 high, 0 medium, 0 low).",
            Findings: findings,
            DryRun: dryRun,
            ValidateRemoteState: validateRemoteState);

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
