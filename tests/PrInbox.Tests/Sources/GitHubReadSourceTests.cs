using System.Net;
using System.Text;
using Octokit;
using Octokit.Internal;
using PrInbox.Core.Credentials;
using PrInbox.Core.Models;
using PrInbox.Sources.GitHub;

namespace PrInbox.Tests.Sources;

public class GitHubArchivedRepositoryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Fast_listing_fetches_archival_once_per_repo_for_reviewer_and_authored_queries(
        bool enterprise, bool authored)
    {
        using var handler = new RepositoryHandler();
        using var adapter = new HttpClientAdapter(() => handler);
        var baseUri = new Uri(enterprise ? "https://ghe.example.com/api/v3/" : "https://api.github.com/");
        var client = new GitHubClient(new Connection(
            new ProductHeaderValue("pr-inbox-tests"), baseUri,
            new InMemoryCredentialStore(new Octokit.Credentials("test-token")), adapter, new SimpleJsonSerializer()));
        var source = new GitHubReadSource(
            "test", baseUri.Host, enterprise, new UnusedTokenProvider());
        var queries = authored ? GitHubReadSource.AuthoredQueries : GitHubReadSource.InboxQueries;

        var first = new List<RemotePullRequest>();
        await foreach (var pr in source.ListFastAsync(client, queries, CancellationToken.None))
        {
            first.Add(pr);
        }

        first.Should().HaveCount(2);
        first.Should().OnlyContain(pr => pr.IsRepoArchived == true);
        handler.RepositoryRequests.Should().Be(1);
        handler.Paths.Should().OnlyContain(p =>
            p == baseUri.AbsolutePath + "search/issues" ||
            p == baseUri.AbsolutePath + "repos/AzureAD/MSAL.PS");

        handler.Archived = false;
        var second = new List<RemotePullRequest>();
        await foreach (var pr in source.ListFastAsync(client, queries, CancellationToken.None))
        {
            second.Add(pr);
        }

        second.Should().OnlyContain(pr => pr.IsRepoArchived == false);
        handler.RepositoryRequests.Should().Be(2, "the cache must expire between syncs");
    }

    [Fact]
    public async Task Repository_lookup_failure_is_not_reported_as_unarchived()
    {
        using var handler = new RepositoryHandler { FailRepositoryLookup = true };
        using var adapter = new HttpClientAdapter(() => handler);
        var client = new GitHubClient(new Connection(new ProductHeaderValue("pr-inbox-tests"), adapter));
        var source = new GitHubReadSource("test", "github.com", false, new UnusedTokenProvider());
        var results = new List<RemotePullRequest>();

        var act = async () =>
        {
            await foreach (var pr in source.ListFastAsync(client, GitHubReadSource.InboxQueries, CancellationToken.None))
            {
                results.Add(pr);
            }
        };

        await act.Should().ThrowAsync<NotFoundException>();
        results.Should().BeEmpty();
    }

    private sealed class RepositoryHandler : HttpMessageHandler
    {
        public int RepositoryRequests { get; private set; }
        public bool Archived { get; set; } = true;
        public bool FailRepositoryLookup { get; init; }
        public List<string> Paths { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (path.EndsWith("/search/issues", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("""
                    {"total_count":2,"incomplete_results":false,"items":[
                      {"id":71,"number":71,"state":"open","title":"Sample",
                       "html_url":"https://github.com/AzureAD/MSAL.PS/pull/71",
                       "created_at":"2026-01-01T00:00:00Z"},
                      {"id":77,"number":77,"state":"open","title":"Another PR",
                       "html_url":"https://github.com/AzureAD/MSAL.PS/pull/77",
                       "created_at":"2026-01-01T00:00:00Z"}
                    ]}
                    """));
            }
            path.Should().EndWith("/repos/AzureAD/MSAL.PS");
            RepositoryRequests++;
            return Task.FromResult(FailRepositoryLookup
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                  { Content = new StringContent("""{"message":"Not Found"}""", Encoding.UTF8, "application/json") }
                : JsonResponse($$"""{"id":123,"full_name":"AzureAD/MSAL.PS","archived":{{Archived.ToString().ToLowerInvariant()}}}"""));
        }

        private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class UnusedTokenProvider : ITokenProvider
    {
        public string SourceId => "test";
        public Task<string> GetTokenAsync(CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<string?> GetAuthenticatedIdentityAsync(CancellationToken ct = default) => throw new InvalidOperationException();
    }
}

public class BotDetectorTests
{
    private readonly BotDetector _detector = new();

    [Fact]
    public void Null_Login_With_Bot_Type_Reports_Bot_Other()
    {
        var (isBot, kind) = _detector.Classify(null, reportedTypeIsBot: true);
        isBot.Should().BeTrue();
        kind.Should().Be(BotKind.Other);
    }

    [Fact]
    public void Null_Login_Without_Bot_Type_Is_Not_A_Bot()
    {
        var (isBot, _) = _detector.Classify(null, reportedTypeIsBot: false);
        isBot.Should().BeFalse();
    }

    [Theory]
    [InlineData("copilot-pull-request-reviewer[bot]", BotKind.CopilotReview)]
    [InlineData("Copilot", BotKind.CopilotReview)]
    [InlineData("copilot[bot]", BotKind.CopilotReview)]
    public void Known_Copilot_Logins_Resolve_To_CopilotReview(string login, BotKind expected)
    {
        var (isBot, kind) = _detector.Classify(login, reportedTypeIsBot: false);
        isBot.Should().BeTrue();
        kind.Should().Be(expected);
    }

    [Fact]
    public void Copilot_Coding_Agent_Login_Resolves_To_CopilotCodingAgent()
    {
        var (isBot, kind) = _detector.Classify("copilot-coding-agent[bot]", reportedTypeIsBot: true);
        isBot.Should().BeTrue();
        kind.Should().Be(BotKind.CopilotCodingAgent);
    }

    [Fact]
    public void Github_Actions_Login_Resolves_To_GitHubActions()
    {
        var (isBot, kind) = _detector.Classify("github-actions[bot]", reportedTypeIsBot: true);
        isBot.Should().BeTrue();
        kind.Should().Be(BotKind.GitHubActions);
    }

    [Fact]
    public void Dependabot_Login_Resolves_To_Dependabot()
    {
        var (isBot, kind) = _detector.Classify("dependabot[bot]", reportedTypeIsBot: true);
        isBot.Should().BeTrue();
        kind.Should().Be(BotKind.Dependabot);
    }

    [Fact]
    public void Bracket_Bot_Login_Defaults_To_Other_Without_Match()
    {
        var (isBot, kind) = _detector.Classify("custom-helper[bot]", reportedTypeIsBot: true);
        isBot.Should().BeTrue();
        kind.Should().Be(BotKind.Other);
    }

    [Fact]
    public void Bracket_Bot_Login_Detected_Even_When_Reported_Bot_Is_False()
    {
        var (isBot, kind) = _detector.Classify("anything[bot]", reportedTypeIsBot: false);
        isBot.Should().BeTrue();
        kind.Should().Be(BotKind.Other);
    }

    [Fact]
    public void Human_Login_Is_Not_A_Bot()
    {
        var (isBot, kind) = _detector.Classify("jmprieur", reportedTypeIsBot: false);
        isBot.Should().BeFalse();
        kind.Should().BeNull();
    }

    [Fact]
    public void Extra_Bot_Logins_Are_Honored()
    {
        var detector = new BotDetector(new[] { "internal-service-account" });
        var (isBot, kind) = detector.Classify("internal-service-account", reportedTypeIsBot: false);
        isBot.Should().BeTrue();
        kind.Should().Be(BotKind.Other);
    }
}

public class GitHubReadSourceParseTests
{
    [Theory]
    [InlineData("https://github.com/octocat/playground/pull/4248", "octocat", "playground", 4248)]
    [InlineData("https://ghe.contoso.com/org/repo-name/pull/12", "org", "repo-name", 12)]
    public void ParseUrl_Splits_Correctly(string url, string owner, string repo, int number)
    {
        var (parsedOwner, parsedRepo, parsedNumber) =
            GitHubReadSource.ParseUrl(url);
        parsedOwner.Should().Be(owner);
        parsedRepo.Should().Be(repo);
        parsedNumber.Should().Be(number);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("https://github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo/issues/5")]
    public void ParseUrl_Throws_On_Malformed_Input(string input)
    {
        var act = () => GitHubReadSource.ParseUrl(input);
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void ParseUrl_Throws_For_AzureDevOps_Url()
    {
        var act = () => GitHubReadSource.ParseUrl("https://dev.azure.com/fabrikam/Context/_git/Private/pullrequest/1234");
        act.Should().Throw<FormatException>();
    }

    /// <summary>
    /// Regression test for the "PR I commented on disappeared from my inbox"
    /// bug. GitHub removes a reviewer from <c>requested_reviewers</c> the
    /// moment they submit any review, so a single <c>review-requested:@me</c>
    /// query was losing PRs the user was actively engaged with. The inbox
    /// must union <c>review-requested:@me</c> with <c>reviewed-by:@me</c>.
    /// </summary>
    [Fact]
    public void InboxQueries_Cover_Both_Pending_And_Engaged_Reviews()
    {
        GitHubReadSource.InboxQueries.Should().BeEquivalentTo(new[]
        {
            "is:pr is:open review-requested:@me",
            "is:pr is:open reviewed-by:@me",
        });
    }

    [Fact]
    public void AuthoredQueries_Cover_Open_PrsIAuthored()
    {
        GitHubReadSource.AuthoredQueries.Should().BeEquivalentTo(new[]
        {
            "is:pr is:open author:@me",
        });
    }
}

/// <summary>
/// Tests for the aggregate review-decision computation used by the authored
/// "My PRs" view (GitHub's reviewDecision semantics, computed from the REST
/// reviews list).
/// </summary>
public class GitHubReviewDecisionTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-06-01T10:00:00Z");

    [Fact]
    public void No_Reviews_Is_Null()
    {
        GitHubReadSource.ComputeReviewDecisionCore(
            Array.Empty<(string?, string?, DateTimeOffset)>()).Should().BeNull();
    }

    [Fact]
    public void Only_Comments_Is_Null()
    {
        var reviews = new (string?, string?, DateTimeOffset)[]
        {
            ("alice", "COMMENTED", T0),
            ("bob", "PENDING", T0),
        };
        GitHubReadSource.ComputeReviewDecisionCore(reviews).Should().BeNull();
    }

    [Fact]
    public void Single_Approval_Is_Approved()
    {
        var reviews = new (string?, string?, DateTimeOffset)[] { ("alice", "APPROVED", T0) };
        GitHubReadSource.ComputeReviewDecisionCore(reviews).Should().Be("approved");
    }

    [Fact]
    public void Any_ChangesRequested_Wins_Over_Approval()
    {
        var reviews = new (string?, string?, DateTimeOffset)[]
        {
            ("alice", "APPROVED", T0),
            ("bob", "CHANGES_REQUESTED", T0),
        };
        GitHubReadSource.ComputeReviewDecisionCore(reviews).Should().Be("changes_requested");
    }

    [Fact]
    public void Latest_Review_Per_User_Wins_Approved_Supersedes_ChangesRequested()
    {
        var reviews = new (string?, string?, DateTimeOffset)[]
        {
            ("alice", "CHANGES_REQUESTED", T0),
            ("alice", "APPROVED", T0.AddHours(1)),
        };
        GitHubReadSource.ComputeReviewDecisionCore(reviews).Should().Be("approved");
    }

    [Fact]
    public void Latest_Review_Per_User_Wins_ChangesRequested_Supersedes_Approved()
    {
        var reviews = new (string?, string?, DateTimeOffset)[]
        {
            ("alice", "APPROVED", T0),
            ("alice", "CHANGES_REQUESTED", T0.AddHours(1)),
        };
        GitHubReadSource.ComputeReviewDecisionCore(reviews).Should().Be("changes_requested");
    }

    [Fact]
    public void Dismissed_Latest_Clears_That_Users_Decision()
    {
        var reviews = new (string?, string?, DateTimeOffset)[]
        {
            ("alice", "CHANGES_REQUESTED", T0),
            ("alice", "DISMISSED", T0.AddHours(1)),
        };
        GitHubReadSource.ComputeReviewDecisionCore(reviews).Should().BeNull();
    }
}
