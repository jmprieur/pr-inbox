using System.Net;
using System.Text;
using Octokit;
using Octokit.Internal;
using PrInbox.Core.Credentials;
using PrInbox.Sources.GitHub;

namespace PrInbox.Tests.Sources;

public sealed class GitHubReadSourceProfileTests
{
    [Fact]
    public async Task Disabled_By_Default_Does_Not_Acquire_Token_Or_Fetch_Profile()
    {
        var source = CreateSource();
        source.RetrieveAuthorDisplayNames.Should().BeFalse();
        (await source.FetchAuthorDisplayNameAsync("octocat", CancellationToken.None)).Should().BeNull();
    }

    [Theory]
    [InlineData("  Alice Smith  ", "Alice Smith")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public async Task Profile_Response_Uses_Trimmed_Name_Or_Username_Fallback(string? name, string? expected)
    {
        using var handler = new ProfileHandler(_ => Task.FromResult(JsonResponse(
            System.Text.Json.JsonSerializer.Serialize(new { login = "octocat", name }))));
        using var http = new HttpClientAdapter(() => handler);
        var client = CreateClient("https://api.github.com/", http);

        var result = await CreateSource().FetchAuthorDisplayNameAsync(client, "octocat", CancellationToken.None);

        result.Should().Be(expected);
        handler.Requests.Should().Equal("https://api.github.com/users/octocat");
    }

    [Fact]
    public async Task Enterprise_Profile_Uses_Enterprise_Endpoint_And_Refetches()
    {
        using var handler = new ProfileHandler(_ => Task.FromResult(JsonResponse("""{"name":"Alice Smith"}""")));
        using var http = new HttpClientAdapter(() => handler);
        var client = CreateClient("https://ghe.example.com/api/v3/", http);
        var source = CreateSource();

        await source.FetchAuthorDisplayNameAsync(client, "alice", CancellationToken.None);
        await source.FetchAuthorDisplayNameAsync(client, "alice", CancellationToken.None);

        handler.Requests.Should().Equal(
            "https://ghe.example.com/api/v3/users/alice",
            "https://ghe.example.com/api/v3/users/alice");
    }

    [Fact]
    public async Task Missing_Profile_Falls_Back_To_Username()
    {
        using var handler = new ProfileHandler(_ => Task.FromResult(JsonResponse(
            """{"message":"Not Found"}""", HttpStatusCode.NotFound)));
        using var http = new HttpClientAdapter(() => handler);

        var name = await CreateSource().FetchAuthorDisplayNameAsync(
            CreateClient("https://api.github.com/", http), "some-bot", CancellationToken.None);

        name.Should().BeNull();
        handler.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Profile_Errors_Are_Not_Silently_Ignored(HttpStatusCode status)
    {
        using var handler = new ProfileHandler(_ => Task.FromResult(JsonResponse(
            """{"message":"Profile request failed"}""", status)));
        using var http = new HttpClientAdapter(() => handler);
        Func<Task> fetch = () => CreateSource().FetchAuthorDisplayNameAsync(
            CreateClient("https://api.github.com/", http), "octocat", CancellationToken.None);

        await fetch.Should().ThrowAsync<ApiException>();
    }

    [Fact]
    public async Task Profile_Lookup_Observes_Cancellation_While_Request_Is_In_Flight()
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new ProfileHandler(_ => response.Task);
        using var http = new HttpClientAdapter(() => handler);
        using var cts = new CancellationTokenSource();
        var fetch = CreateSource().FetchAuthorDisplayNameAsync(
            CreateClient("https://api.github.com/", http), "octocat", cts.Token);
        cts.Cancel();
        Func<Task> wait = () => fetch;

        await wait.Should().ThrowAsync<OperationCanceledException>();
        response.SetResult(JsonResponse("""{"name":"Alice"}"""));
    }

    private static GitHubReadSource CreateSource() =>
        new("gh-test", "github.com", false, new RejectingTokenProvider());

    private static GitHubClient CreateClient(string baseUrl, HttpClientAdapter http) =>
        new(new Connection(new ProductHeaderValue("PrInbox.Tests"), new Uri(baseUrl),
            new InMemoryCredentialStore(Octokit.Credentials.Anonymous), http, new SimpleJsonSerializer()));

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class ProfileHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return send(request);
        }
    }

    private sealed class RejectingTokenProvider : ITokenProvider
    {
        public string SourceId => "gh-test";
        public Task<string> GetTokenAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("Disabled profile lookups must not acquire tokens.");
        public Task<string?> GetAuthenticatedIdentityAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
