using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PrInbox.Core.Findings;
using PrInbox.Core.Models;
using PrInbox.Core.Storage;
using PrInbox.Web.Services;

namespace PrInbox.Tests.Web;

public sealed class LocalFindingVerifierTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"local-verifier-{Guid.NewGuid():N}");

    public LocalFindingVerifierTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Verify_DropsCandidateWhenPostChangeSourceFixesIt()
    {
        var provider = new StubFileProvider(
            """
            $targetEntryCount = @($target.PSObject.Properties).Count
            if ($targetEntryCount -eq 0 -and $directDeps.Count -gt 0) {
                $escalations += [pscustomobject]@{
                    reason = 'assets-file-has-no-resolved-packages'
                }
            }
            """);
        var handler = new StubHandler(
            ChatResponse("""
                {"keep":false,"reason":"The new guard already escalates this case.","evidence_line":null,"evidence":null}
                """));
        var verifier = new LocalFindingVerifier(
            provider,
            new StubHttpClientFactory(handler));
        var transcript = Path.Combine(_root, "transcript.txt");
        File.WriteAllText(transcript, "");

        var result = await verifier.VerifyAsync(
            new Uri("http://127.0.0.1:1234/v1/chat/completions"),
            "qwen2.5-coder-14b",
            SamplePr(),
            "abcdef1",
            """
            diff --git a/scripts/Reconcile.ps1 b/scripts/Reconcile.ps1
            --- a/scripts/Reconcile.ps1
            +++ b/scripts/Reconcile.ps1
            @@ -1,1 +1,6 @@
             $targetEntryCount = @($target.PSObject.Properties).Count
            +if ($targetEntryCount -eq 0 -and $directDeps.Count -gt 0) {
            +    $escalations += [pscustomobject]@{
            +        reason = 'assets-file-has-no-resolved-packages'
            +    }
            +}
            """,
            [
                new Finding
                {
                    Id = "local-01",
                    Severity = FindingSeverity.High,
                    Confidence = FindingConfidence.High,
                    FoundBy = ["qwen2.5-coder-14b"],
                    File = "scripts/Reconcile.ps1",
                    Line = 2,
                    Title = "Empty graph reported idempotent",
                    Body = "The empty graph is not handled.",
                },
            ],
            32_768,
            isReasoning: false,
            transcriptPath: transcript,
            reviewPass: 1,
            progress: null,
            ct: CancellationToken.None);

        result.Findings.Should().BeEmpty();
        result.Warnings.Should().Equal(
            "Verifier dropped local finding 'Empty graph reported idempotent': " +
            "The new guard already escalates this case.");
        File.ReadAllText(transcript).Should()
            .Contain("POST-CHANGE SOURCE")
            .And.Contain("RAW VERIFICATION RESPONSE");
    }

    [Fact]
    public async Task Verify_KeepsCandidateWithExactAddedLineEvidence()
    {
        var content = """
            existing
            return input.Trim();
            """;
        var provider = new StubFileProvider(content);
        var handler = new StubHandler(
            ChatResponse("""
                {"keep":true,"reason":"Null still reaches Trim.","evidence_line":2,"evidence":"return input.Trim();"}
                """));
        var verifier = new LocalFindingVerifier(
            provider,
            new StubHttpClientFactory(handler));
        var transcript = Path.Combine(_root, "transcript.txt");
        File.WriteAllText(transcript, "");

        var result = await verifier.VerifyAsync(
            new Uri("http://127.0.0.1:1234/v1/chat/completions"),
            "qwen2.5-coder-14b",
            SamplePr(),
            "abcdef1",
            """
            diff --git a/src/a.cs b/src/a.cs
            --- a/src/a.cs
            +++ b/src/a.cs
            @@ -1,1 +1,2 @@
             existing
            +return input.Trim();
            """,
            [
                new Finding
                {
                    Id = "local-01",
                    Severity = FindingSeverity.High,
                    Confidence = FindingConfidence.High,
                    FoundBy = ["qwen2.5-coder-14b"],
                    File = "src/a.cs",
                    Line = 2,
                    Title = "Null dereference",
                    Body = "Null input throws.",
                },
            ],
            32_768,
            isReasoning: false,
            transcriptPath: transcript,
            reviewPass: 1,
            progress: null,
            ct: CancellationToken.None);

        result.Findings.Should().ContainSingle();
        result.Findings[0].Line.Should().Be(2);
        result.Warnings.Should().BeEmpty();
    }

    private static PullRequestRow SamplePr() => new(
        new PrIdentity(
            "https://github.com/owner/repo/pull/1",
            "gh.com:1#1"),
        SourceKind.GitHub,
        "gh.com",
        "owner/repo",
        1,
        "test",
        "author",
        "https://github.com/owner/repo/pull/1",
        PullRequestStatus.Open,
        TrackingReason.Assigned,
        "owner",
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        EnrichState.Enriched,
        null,
        null,
        null);

    private static string ChatResponse(string content) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { message = new { content } },
            },
        });

    private sealed class StubFileProvider : IReviewPatchProvider
    {
        private readonly string _content;

        public StubFileProvider(string content)
        {
            _content = content;
        }

        public Task<ReviewPatchResult> GetPatchAsync(
            PullRequestRow pr,
            int maxCharacters,
            CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<PostChangeFileResult> GetPostChangeFileAsync(
            PullRequestRow pr,
            string path,
            string headSha,
            int maxCharacters,
            CancellationToken ct) =>
            Task.FromResult(new PostChangeFileResult(
                _content,
                false,
                null));
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public StubHttpClientFactory(HttpMessageHandler handler)
        {
            _client = new HttpClient(handler);
        }

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _response;

        public StubHandler(string response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    _response,
                    Encoding.UTF8,
                    "application/json"),
            });
    }
}
