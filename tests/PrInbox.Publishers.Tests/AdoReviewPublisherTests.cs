using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PrInbox.Core.Findings;
using Xunit;

namespace PrInbox.Publishers.Tests;

public sealed class AdoReviewPublisherTests
{
    [Fact]
    public async Task DryRun_makes_zero_http_requests()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException(
            "DryRun must not make any HTTP call."));
        using var http = new HttpClient(handler);
        var token = new FakeTokenProvider(_ => throw new InvalidOperationException(
            "DryRun must not even fetch a token."));

        var publisher = new AdoReviewPublisher(
            token, http, identityUsed: "azurecli",
            log: NullLogger<AdoReviewPublisher>.Instance);

        var req = new PublishRequest(
            PrUrl: "https://dev.azure.com/mseng/Context/_git/Private/pullrequest/100",
            RunId: 7,
            HeadShaAtAuthoring: "abc123",
            ReviewBodyHeader: "header",
            Findings: new[] { Finding("f01", anchorable: true) },
            DryRun: true,
            ValidateRemoteState: false);

        var result = await publisher.PublishAsync(req, CancellationToken.None);

        result.Posted.Should().BeFalse();
        result.Errors.Should().BeEmpty();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Live_posts_header_then_inline_threads()
    {
        var responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        // 1. GET repositoryId by name
        responses.Enqueue(req =>
        {
            req.Method.Should().Be(HttpMethod.Get);
            req.RequestUri!.ToString().Should().Contain("/_apis/git/repositories/Private");
            return Ok(@"{""id"":""00000000-0000-0000-0000-000000000123""}");
        });
        // 2. POST header thread (no threadContext)
        responses.Enqueue(req =>
        {
            req.Method.Should().Be(HttpMethod.Post);
            req.RequestUri!.ToString().Should().Contain("00000000-0000-0000-0000-000000000123/pullRequests/100/threads");
            var doc = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
            doc.RootElement.TryGetProperty("threadContext", out var ctx).Should().BeFalse(
                "header thread should not carry a threadContext");
            doc.RootElement.GetProperty("comments")[0].GetProperty("content").GetString()
                .Should().Contain("header");
            return Ok(@"{""id"":555}");
        });
        // 3. POST inline thread (with threadContext)
        responses.Enqueue(req =>
        {
            var doc = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
            var ctx = doc.RootElement.GetProperty("threadContext");
            ctx.GetProperty("filePath").GetString().Should().Be("/src/Foo.cs");
            ctx.GetProperty("rightFileStart").GetProperty("line").GetInt32().Should().Be(42);
            return Ok(@"{""id"":777}");
        });

        var handler = new RecordingHandler(req => responses.Dequeue()(req));
        using var http = new HttpClient(handler);
        var token = new FakeTokenProvider(_ => "tk");

        var publisher = new AdoReviewPublisher(
            token, http, identityUsed: "azurecli",
            log: NullLogger<AdoReviewPublisher>.Instance);

        var req = new PublishRequest(
            PrUrl: "https://dev.azure.com/mseng/Context/_git/Private/pullrequest/100",
            RunId: 7,
            HeadShaAtAuthoring: "abc123",
            ReviewBodyHeader: "**header**",
            Findings: new[] { Finding("f01", anchorable: true) },
            DryRun: false,
            ValidateRemoteState: false);

        var result = await publisher.PublishAsync(req, CancellationToken.None);

        result.Posted.Should().BeTrue();
        result.PlatformReviewId.Should().Be("555");
        result.InlineCount.Should().Be(1);
        handler.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task Approve_event_is_rejected_on_ADO()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException(
            "Should fail before any HTTP call."));
        using var http = new HttpClient(handler);
        var token = new FakeTokenProvider(_ => "tk");

        var publisher = new AdoReviewPublisher(
            token, http, identityUsed: "azurecli",
            log: NullLogger<AdoReviewPublisher>.Instance);

        var req = new PublishRequest(
            PrUrl: "https://dev.azure.com/mseng/Context/_git/Private/pullrequest/100",
            RunId: 7,
            HeadShaAtAuthoring: "abc123",
            ReviewBodyHeader: "header",
            Findings: new[] { Finding("f01", anchorable: true) },
            DryRun: false,
            ValidateRemoteState: false,
            Event: ReviewEvent.Approve);

        var result = await publisher.PublishAsync(req, CancellationToken.None);
        result.Posted.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Azure DevOps", StringComparison.OrdinalIgnoreCase));
        handler.Requests.Should().BeEmpty();
    }

    private static FindingToPost Finding(string id, bool anchorable)
        => new(
            Id: id,
            Severity: FindingSeverity.High,
            Confidence: FindingConfidence.Medium,
            FoundBy: new[] { "opus" },
            File: "src/Foo.cs",
            Line: 42,
            LineEnd: 44,
            DiffAnchorable: anchorable,
            Title: "An issue",
            Body: "Details.",
            SuggestedInline: null);

    private static HttpResponseMessage Ok(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
}
