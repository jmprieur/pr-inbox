using System.Net;
using System.Text;
using PrInbox.Core.Credentials;
using PrInbox.Core.Models;
using PrInbox.Sources.AzureDevOps;
using PrInbox.Sources.GitHub;

namespace PrInbox.Tests.Sources;

public class AzureDevOpsReadSourceTests
{
    [Fact]
    public void ParseAdoUrl_Extracts_RepoName_And_Number()
    {
        var (repo, number) = AzureDevOpsReadSource.ParseAdoUrl(
            "https://dev.azure.com/mseng/Context/_git/Private/pullrequest/1234");
        repo.Should().Be("Private");
        number.Should().Be(1234);
    }

    [Fact]
    public void ParseAdoUrl_Throws_On_GitHub_Url()
    {
        var act = () => AzureDevOpsReadSource.ParseAdoUrl("https://github.com/owner/repo/pull/1");
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public async Task ListAssignedFastAsync_Resolves_Profile_Then_Lists_Project()
    {
        var handler = new RecordingHandler();
        handler.Enqueue("""
            { "id": "11111111-aaaa-bbbb-cccc-222222222222", "displayName": "Jean-Marc",
              "emailAddress": "jm@example.com", "publicAlias": "33333333-aaaa-bbbb-cccc-444444444444" }
            """);
        handler.Enqueue("""
            { "count": 1, "value": [{
                "pullRequestId": 42,
                "title": "Fix things",
                "status": "active",
                "creationDate": "2026-05-01T12:00:00Z",
                "createdBy": { "uniqueName": "alice@example.com", "displayName": "Alice" },
                "repository": {
                    "id": "55555555-aaaa-bbbb-cccc-666666666666",
                    "name": "MyRepo",
                    "project": { "id": "77777777-aaaa-bbbb-cccc-888888888888", "name": "Context" }
                },
                "reviewers": []
            }] }
            """);

        var (source, _) = BuildSource(handler);
        var results = new List<RemotePullRequest>();
        await foreach (var pr in source.ListAssignedFastAsync(CancellationToken.None))
        {
            results.Add(pr);
        }

        results.Should().HaveCount(1);
        var only = results[0];
        only.Number.Should().Be(42);
        only.Title.Should().Be("Fix things");
        only.AuthorLogin.Should().Be("alice@example.com");
        only.Url.Should().Be("https://dev.azure.com/mseng/Context/_git/MyRepo/pullrequest/42");
        only.DisplayRepo.Should().Be("Context/MyRepo");
        only.Status.Should().Be(PullRequestStatus.Open);
        only.SourceKind.Should().Be(SourceKind.AzureDevOps);
        only.SourceId.Should().Be("ado:mseng/Context");

        handler.Requests[0].RequestUri!.ToString().Should().Contain("vssps.dev.azure.com");
        handler.Requests[1].RequestUri!.ToString().Should()
            .Contain("/mseng/Context/_apis/git/pullrequests")
            .And.Contain("searchCriteria.reviewerId=11111111-aaaa-bbbb-cccc-222222222222")
            .And.Contain("searchCriteria.status=active");
    }

    [Fact]
    public async Task ListAuthoredFastAsync_Resolves_Profile_Then_Lists_AuthoredPrs()
    {
        var handler = new RecordingHandler();
        handler.Enqueue("""
            { "id": "11111111-aaaa-bbbb-cccc-222222222222", "displayName": "Jean-Marc",
              "emailAddress": "jm@example.com", "publicAlias": "33333333-aaaa-bbbb-cccc-444444444444" }
            """);
        handler.Enqueue("""
            { "count": 1, "value": [{
                "pullRequestId": 77,
                "title": "My own PR",
                "status": "active",
                "creationDate": "2026-05-02T12:00:00Z",
                "createdBy": { "uniqueName": "jm@example.com", "displayName": "Jean-Marc" },
                "repository": {
                    "id": "55555555-aaaa-bbbb-cccc-666666666666",
                    "name": "MyRepo",
                    "project": { "id": "77777777-aaaa-bbbb-cccc-888888888888", "name": "Context" }
                },
                "reviewers": []
            }] }
            """);

        var (source, _) = BuildSource(handler);
        source.Capabilities.SupportsAuthoredInbox.Should().BeTrue();

        var results = new List<RemotePullRequest>();
        await foreach (var pr in source.ListAuthoredFastAsync(CancellationToken.None))
        {
            results.Add(pr);
        }

        results.Should().HaveCount(1);
        results[0].Number.Should().Be(77);
        results[0].AuthorLogin.Should().Be("jm@example.com");

        handler.Requests[1].RequestUri!.ToString().Should()
            .Contain("/mseng/Context/_apis/git/pullrequests")
            .And.Contain("searchCriteria.creatorId=11111111-aaaa-bbbb-cccc-222222222222")
            .And.Contain("searchCriteria.status=active");
    }

    [Fact]
    public async Task ListFast_Maps_IsDraft_From_AdoListTier()
    {
        var handler = new RecordingHandler();
        handler.Enqueue("""{ "id": "self-id" }""");
        handler.Enqueue("""
            { "count": 2, "value": [
                { "pullRequestId": 1, "title": "Draft one", "status": "active", "isDraft": true,
                  "creationDate": "2026-05-01T12:00:00Z",
                  "createdBy": { "uniqueName": "jm@example.com" },
                  "repository": { "id": "55555555-aaaa-bbbb-cccc-666666666666", "name": "MyRepo",
                                  "project": { "id": "77777777-aaaa-bbbb-cccc-888888888888", "name": "Context" } },
                  "reviewers": [] },
                { "pullRequestId": 2, "title": "Ready two", "status": "active", "isDraft": false,
                  "creationDate": "2026-05-01T12:00:00Z",
                  "createdBy": { "uniqueName": "jm@example.com" },
                  "repository": { "id": "55555555-aaaa-bbbb-cccc-666666666666", "name": "MyRepo",
                                  "project": { "id": "77777777-aaaa-bbbb-cccc-888888888888", "name": "Context" } },
                  "reviewers": [] }
            ] }
            """);

        var (source, _) = BuildSource(handler);
        var results = new List<RemotePullRequest>();
        await foreach (var pr in source.ListAssignedFastAsync(CancellationToken.None))
        {
            results.Add(pr);
        }

        results.Single(r => r.Number == 1).IsDraft.Should().BeTrue();
        results.Single(r => r.Number == 2).IsDraft.Should().BeFalse();
    }

    [Fact]
    public async Task ListAssignedFastAsync_Pages_Until_Short_Page()
    {
        var handler = new RecordingHandler();
        handler.Enqueue("""
            { "id": "self-id", "displayName": "Me" }
            """);
        var fullPage = BuildPrPageJson(itemCount: 100, startId: 1);
        var shortPage = BuildPrPageJson(itemCount: 23, startId: 101);
        handler.Enqueue(fullPage);
        handler.Enqueue(shortPage);

        var (source, _) = BuildSource(handler);
        var results = new List<RemotePullRequest>();
        await foreach (var pr in source.ListAssignedFastAsync(CancellationToken.None))
        {
            results.Add(pr);
        }

        results.Should().HaveCount(123);
        handler.Requests.Should().HaveCount(3); // profile + 2 pages
        handler.Requests[1].RequestUri!.Query.Should().Contain("$skip=0");
        handler.Requests[2].RequestUri!.Query.Should().Contain("$skip=100");
    }

    [Fact]
    public async Task EnrichAsync_Calls_Detail_Threads_And_Commits_In_Parallel()
    {
        var handler = new RecordingHandler();
        handler.Enqueue("""
            { "id": "self-id" }
            """);
        // Detail
        handler.Enqueue("""
            { "pullRequestId": 42, "status": "active", "isDraft": false,
              "lastMergeSourceCommit": { "commitId": "deadbeef" },
              "lastMergeTargetCommit": { "commitId": "cafebabe" },
              "lastMergeCommit": { "commitId": "1234abcd" },
              "repository": { "id": "55555555-aaaa-bbbb-cccc-666666666666", "name": "MyRepo",
                              "project": { "id": "77777777-aaaa-bbbb-cccc-888888888888", "name": "Context" } },
              "reviewers": [ { "id": "self-id", "vote": 0 } ] }
            """);
        // Threads
        handler.Enqueue("""
            { "count": 2, "value": [
                { "id": 1, "status": "active", "isDeleted": false,
                  "comments": [{ "id": 11, "content": "looks good?", "publishedDate": "2026-05-01T12:00:00Z",
                                  "commentType": "text",
                                  "author": { "uniqueName": "alice@example.com", "displayName": "Alice", "isContainer": false } }] },
                { "id": 2, "status": "fixed", "isDeleted": false,
                  "comments": [{ "id": 22, "content": "vote", "publishedDate": "2026-05-01T13:00:00Z",
                                  "commentType": "system",
                                  "author": { "displayName": "System" } }] }
            ] }
            """);
        // Commits
        handler.Enqueue("""
            { "count": 1, "value": [{ "commitId": "deadbeef",
                                       "author": { "name": "Alice", "email": "alice@example.com", "date": "2026-05-01T10:00:00Z" },
                                       "comment": "Initial commit\nmore body" }] }
            """);

        var (source, _) = BuildSource(handler);
        // ListAssignedFastAsync to seed reviewer id (so reviewer-state interpretation can use it)
        await foreach (var _ in source.ListAssignedFastAsync(CancellationToken.None)) { }

        var id = new PrIdentity(
            Url: "https://dev.azure.com/mseng/Context/_git/MyRepo/pullrequest/42",
            Stable: "ado:mseng/77777777-aaaa-bbbb-cccc-888888888888/55555555-aaaa-bbbb-cccc-666666666666#42");

        // Reset handler queue with detail/threads/commits responses (the first
        // listing exhausted the profile + an empty PR-list page). We just
        // re-enqueue the three enrichment responses in order.
        handler.Reset();
        handler.Enqueue("""
            { "pullRequestId": 42, "status": "active", "isDraft": false,
              "lastMergeSourceCommit": { "commitId": "deadbeef" },
              "lastMergeTargetCommit": { "commitId": "cafebabe" },
              "lastMergeCommit": { "commitId": "1234abcd" },
              "repository": { "id": "55555555-aaaa-bbbb-cccc-666666666666", "name": "MyRepo",
                              "project": { "id": "77777777-aaaa-bbbb-cccc-888888888888", "name": "Context" } },
              "reviewers": [ { "id": "self-id", "vote": 10 } ] }
            """);
        handler.Enqueue("""
            { "count": 2, "value": [
                { "id": 1, "status": "active", "isDeleted": false,
                  "comments": [{ "id": 11, "content": "looks good?", "publishedDate": "2026-05-01T12:00:00Z",
                                  "commentType": "text",
                                  "author": { "uniqueName": "alice@example.com", "displayName": "Alice", "isContainer": false } }] },
                { "id": 2, "status": "fixed", "isDeleted": false,
                  "comments": [{ "id": 22, "content": "vote", "publishedDate": "2026-05-01T13:00:00Z",
                                  "commentType": "system",
                                  "author": { "displayName": "System" } }] }
            ] }
            """);
        handler.Enqueue("""
            { "count": 1, "value": [{ "commitId": "deadbeef",
                                       "author": { "name": "Alice", "email": "alice@example.com", "date": "2026-05-01T10:00:00Z" },
                                       "comment": "Initial commit\nmore body" }] }
            """);

        var bundle = await source.EnrichAsync(id, CancellationToken.None);

        bundle.Detail.HeadSha.Should().Be("deadbeef");
        bundle.Detail.BaseSha.Should().Be("cafebabe");
        bundle.Detail.MergeBaseSha.Should().Be("1234abcd");
        bundle.Detail.ReviewerState.Should().Be(ReviewerState.Approved); // vote=10
        bundle.Detail.OrderedCommitShas.Should().Equal("deadbeef");

        // Only the human-authored thread should survive — the all-system one is filtered.
        bundle.Threads.Should().HaveCount(1);
        bundle.Threads[0].PlatformThreadId.Should().Be("ado-thread:1");
        bundle.Threads[0].Kind.Should().Be(ThreadKind.AdoThread);
        bundle.Threads[0].AuthorLogin.Should().Be("alice@example.com");
        bundle.Threads[0].IsResolved.Should().BeFalse();
    }

    [Fact]
    public async Task EnrichAsync_Reviewer_Votes_Map_To_ReviewerState()
    {
        // Spot-check the vote-to-state mapping with a single PR detail call.
        (string voteJson, ReviewerState expected)[] cases =
        {
            ("10",  ReviewerState.Approved),
            ("5",   ReviewerState.ApprovedWithSuggestions),
            ("-5",  ReviewerState.Waiting),
            ("-10", ReviewerState.ChangesRequested),
            ("0",   ReviewerState.Requested),
        };

        foreach (var (voteJson, expected) in cases)
        {
            var handler = new RecordingHandler();
            // Profile (resolves selfId)
            handler.Enqueue("""{ "id": "self-id" }""");
            // List (empty)
            handler.Enqueue("""{ "count": 0, "value": [] }""");
            var (source, _) = BuildSource(handler);
            await foreach (var _ in source.ListAssignedFastAsync(CancellationToken.None)) { }

            handler.Reset();
            handler.Enqueue($$"""
                { "pullRequestId": 42, "status": "active",
                  "lastMergeSourceCommit": { "commitId": "h" },
                  "lastMergeTargetCommit": { "commitId": "b" },
                  "repository": { "id": "11111111-1111-1111-1111-111111111111", "name": "R",
                                  "project": { "id": "22222222-2222-2222-2222-222222222222", "name": "P" } },
                  "reviewers": [ { "id": "self-id", "vote": {{voteJson}} } ] }
                """);
            handler.Enqueue("""{ "count": 0, "value": [] }""");
            handler.Enqueue("""{ "count": 0, "value": [] }""");

            var bundle = await source.EnrichAsync(
                new PrIdentity("https://dev.azure.com/mseng/Context/_git/R/pullrequest/42", "stable"),
                CancellationToken.None);
            bundle.Detail.ReviewerState.Should().Be(expected, $"vote was {voteJson}");
        }
    }

    [Fact]
    public async Task MapThreads_Resolves_Status_From_Common_Values()
    {
        var handler = new RecordingHandler();
        handler.Enqueue("""{ "id": "self-id" }""");
        handler.Enqueue("""{ "count": 0, "value": [] }""");
        var (source, _) = BuildSource(handler);
        await foreach (var _ in source.ListAssignedFastAsync(CancellationToken.None)) { }

        handler.Reset();
        handler.Enqueue("""
            { "pullRequestId": 1, "status": "active",
              "lastMergeSourceCommit": { "commitId": "h" },
              "lastMergeTargetCommit": { "commitId": "b" },
              "repository": { "id": "00000000-0000-0000-0000-000000000001", "name": "R",
                              "project": { "id": "00000000-0000-0000-0000-000000000002", "name": "P" } },
              "reviewers": [] }
            """);
        handler.Enqueue("""
            { "count": 4, "value": [
                { "id": 1, "status": "active",  "isDeleted": false,
                  "comments": [{ "id": 1, "publishedDate": "2026-05-01T12:00:00Z", "commentType": "text",
                                 "author": { "uniqueName": "u@e.com" } }] },
                { "id": 2, "status": "fixed",   "isDeleted": false,
                  "comments": [{ "id": 2, "publishedDate": "2026-05-01T12:00:00Z", "commentType": "text",
                                 "author": { "uniqueName": "u@e.com" } }] },
                { "id": 3, "status": "wontFix", "isDeleted": false,
                  "comments": [{ "id": 3, "publishedDate": "2026-05-01T12:00:00Z", "commentType": "text",
                                 "author": { "uniqueName": "u@e.com" } }] },
                { "id": 4, "status": "byDesign","isDeleted": false,
                  "comments": [{ "id": 4, "publishedDate": "2026-05-01T12:00:00Z", "commentType": "text",
                                 "author": { "uniqueName": "u@e.com" } }] }
            ] }
            """);
        handler.Enqueue("""{ "count": 0, "value": [] }""");

        var bundle = await source.EnrichAsync(
            new PrIdentity("https://dev.azure.com/mseng/Context/_git/R/pullrequest/1", "stable"),
            CancellationToken.None);

        bundle.Threads.Should().HaveCount(4);
        bundle.Threads.Single(t => t.PlatformThreadId == "ado-thread:1").IsResolved.Should().BeFalse();
        bundle.Threads.Single(t => t.PlatformThreadId == "ado-thread:2").IsResolved.Should().BeTrue();
        bundle.Threads.Single(t => t.PlatformThreadId == "ado-thread:3").IsResolved.Should().BeTrue();
        bundle.Threads.Single(t => t.PlatformThreadId == "ado-thread:4").IsResolved.Should().BeTrue();
    }

    [Fact]
    public async Task Bot_Classification_Honors_IsContainer()
    {
        var handler = new RecordingHandler();
        handler.Enqueue("""{ "id": "self-id" }""");
        handler.Enqueue("""{ "count": 0, "value": [] }""");
        var (source, _) = BuildSource(handler);
        await foreach (var _ in source.ListAssignedFastAsync(CancellationToken.None)) { }

        handler.Reset();
        handler.Enqueue("""
            { "pullRequestId": 1, "status": "active",
              "lastMergeSourceCommit": { "commitId": "h" },
              "lastMergeTargetCommit": { "commitId": "b" },
              "repository": { "id": "00000000-0000-0000-0000-000000000001", "name": "R",
                              "project": { "id": "00000000-0000-0000-0000-000000000002", "name": "P" } },
              "reviewers": [] }
            """);
        handler.Enqueue("""
            { "count": 1, "value": [
                { "id": 1, "status": "active", "isDeleted": false,
                  "comments": [{ "id": 1, "publishedDate": "2026-05-01T12:00:00Z", "commentType": "text",
                                 "author": { "uniqueName": "build-svc", "displayName": "Build Service", "isContainer": true } }] }
            ] }
            """);
        handler.Enqueue("""{ "count": 0, "value": [] }""");

        var bundle = await source.EnrichAsync(
            new PrIdentity("https://dev.azure.com/mseng/Context/_git/R/pullrequest/1", "stable"),
            CancellationToken.None);

        bundle.Threads[0].IsBot.Should().BeTrue("isContainer=true means service account / bot");
        bundle.Threads[0].BotKind.Should().Be(BotKind.Other);
    }

    private static (AzureDevOpsReadSource Source, RecordingHandler Handler) BuildSource(RecordingHandler handler)
    {
        var tokenProvider = new FakeTokenProvider("ado:mseng/Context");
        var http = new HttpClient(handler);
        var source = new AzureDevOpsReadSource(
            "ado:mseng/Context", "mseng", "Context", tokenProvider, botDetector: null, http: http);
        return (source, handler);
    }

    private static string BuildPrPageJson(int itemCount, int startId)
    {
        var sb = new StringBuilder();
        sb.Append("{ \"count\": ").Append(itemCount).Append(", \"value\": [");
        for (var i = 0; i < itemCount; i++)
        {
            if (i > 0) sb.Append(',');
            var id = startId + i;
            sb.Append($$"""
                { "pullRequestId": {{id}}, "title": "PR {{id}}", "status": "active",
                  "creationDate": "2026-05-01T12:00:00Z",
                  "repository": { "id": "00000000-0000-0000-0000-000000000001", "name": "R",
                                  "project": { "id": "00000000-0000-0000-0000-000000000002", "name": "Context" } },
                  "reviewers": [] }
                """);
        }
        sb.Append("] }");
        return sb.ToString();
    }

    /// <summary>HTTP handler that returns enqueued canned responses in order.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new();
        public List<HttpRequestMessage> Requests { get; } = new();

        public void Enqueue(string json) => _responses.Enqueue(json);

        public void Reset()
        {
            _responses.Clear();
            Requests.Clear();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new HttpRequestMessage(request.Method, request.RequestUri));
            if (_responses.Count == 0)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("no canned response queued")
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class FakeTokenProvider : ITokenProvider
    {
        public FakeTokenProvider(string sourceId) { SourceId = sourceId; }
        public string SourceId { get; }
        public Task<string> GetTokenAsync(CancellationToken ct = default) => Task.FromResult("fake-token");
        public Task<string?> GetAuthenticatedIdentityAsync(CancellationToken ct = default) => Task.FromResult<string?>("fake@example.com");
    }
}
