using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PrInbox.Core.Credentials;
using PrInbox.Core.Findings;
using PrInbox.Core.Models;
using PrInbox.Core.Reviewing;
using PrInbox.Core.Storage;
using PrInbox.Web.Services;

namespace PrInbox.Tests.Web;

public sealed class LocalReviewRunnerTests
{
    [Fact]
    public void ParseStatusJson_ReturnsLoopbackEndpoint()
    {
        var json = """
            {
              "running": true,
              "webUrls": ["http://127.0.0.1:62058"]
            }
            """;

        FoundryLocalEndpointResolver.ParseStatusJson(json)
            .Should().Be("http://127.0.0.1:62058");
    }

    [Fact]
    public void FoundryRuntime_RecognizesServerStateAndCachedModelNames()
    {
        FoundryLocalRuntime.IsServerRunning(
            """{"running":true,"state":"ready"}""").Should().BeTrue();
        FoundryLocalRuntime.IsServerRunning(
            """{"running":false,"state":"not_running"}""").Should().BeFalse();

        var cached = """
            {
              "variants": [
                {
                  "alias": "qwen2.5-coder-7b",
                  "variantName": "qwen2.5-coder-7b-instruct-qnn-npu",
                  "variantId": "qwen2.5-coder-7b-instruct-qnn-npu:1",
                  "cached": true
                }
              ]
            }
            """;

        FoundryLocalRuntime.IsModelCached(
            cached, "qwen2.5-coder-7b").Should().BeTrue();
        FoundryLocalRuntime.IsModelCached(
            cached, "qwen2.5-coder-7b-instruct-qnn-npu").Should().BeTrue();
        FoundryLocalRuntime.IsModelCached(
            cached, "qwen2.5-coder-14b").Should().BeFalse();
    }

    [Fact]
    public async Task FoundryRuntime_StartsServerAndLoadsCachedModel()
    {
        var cli = new StubFoundryCliRunner(
            new FoundryCliResult(
                0, """{"running":false,"state":"not_running"}""", ""),
            new FoundryCliResult(0, "started", ""),
            new FoundryCliResult(
                0,
                """{"variants":[{"alias":"qwen2.5-coder-7b","cached":true}]}""",
                ""),
            new FoundryCliResult(
                0,
                """{"model":{"alias":"qwen2.5-coder-7b","id":"qwen2.5-coder-7b-instruct-qnn-npu:1","contextLength":32768}}""",
                ""),
            new FoundryCliResult(0, """{"variants":[]}""", ""),
            new FoundryCliResult(0, "loaded", ""));
        var runtime = new FoundryLocalRuntime(cli);

        var result = await runtime.PrepareAsync(
            configuredEndpoint: "",
            model: "qwen2.5-coder-7b",
            timeoutSeconds: 600,
            ct: CancellationToken.None);

        result.ContextLength.Should().Be(32_768);
        cli.Commands.Should().Equal(
            "server status --output json",
            "server start",
            "model list --cached --variants --output json --limit 500",
            "model info qwen2.5-coder-7b --output json",
            "model list --loaded --variants --output json --limit 500",
            "model load qwen2.5-coder-7b");
    }

    [Fact]
    public async Task FoundryRuntime_UnloadsSiblingVariantBeforeExactLoad()
    {
        var cli = new StubFoundryCliRunner(
            new FoundryCliResult(
                0, """{"running":true,"state":"ready"}""", ""),
            new FoundryCliResult(
                0,
                """{"variants":[{"alias":"gpt-oss-20b","variantId":"gpt-oss-20b-generic-cpu:1","cached":true}]}""",
                ""),
            new FoundryCliResult(
                0,
                """{"model":{"alias":"gpt-oss-20b","id":"gpt-oss-20b-generic-cpu:1","contextLength":131072}}""",
                ""),
            new FoundryCliResult(
                0,
                """{"variants":[{"alias":"gpt-oss-20b","variantId":"gpt-oss-20b-generic-gpu:1"},{"alias":"gpt-oss-20b","variantId":"gpt-oss-20b-generic-cpu:1"}]}""",
                ""),
            new FoundryCliResult(0, "unloaded", ""),
            new FoundryCliResult(0, "loaded", ""));
        var runtime = new FoundryLocalRuntime(cli);

        await runtime.PrepareAsync(
            configuredEndpoint: "",
            model: "gpt-oss-20b-generic-cpu",
            timeoutSeconds: 600,
            ct: CancellationToken.None);

        cli.Commands.Should().ContainInOrder(
            "model info gpt-oss-20b-generic-cpu --output json",
            "model list --loaded --variants --output json --limit 500",
            "model unload gpt-oss-20b-generic-gpu:1",
            "model load gpt-oss-20b-generic-cpu");
        cli.Commands.Should().NotContain(
            "model unload gpt-oss-20b-generic-cpu:1");
    }

    [Fact]
    public async Task FoundryRuntime_MissingModelDoesNotDownloadAndShowsInstructions()
    {
        var cli = new StubFoundryCliRunner(
            new FoundryCliResult(
                0, """{"running":true,"state":"ready"}""", ""),
            new FoundryCliResult(0, """{"variants":[]}""", ""));
        var runtime = new FoundryLocalRuntime(cli);

        var act = () => runtime.PrepareAsync(
            configuredEndpoint: "",
            model: "qwen2.5-coder-14b",
            timeoutSeconds: 600,
            ct: CancellationToken.None);

        var error = await act.Should()
            .ThrowAsync<LocalModelNotCachedException>();
        error.Which.Message.Should()
            .Contain("foundry model download qwen2.5-coder-14b");
        cli.Commands.Should().NotContain(c => c.Contains("download"));
    }

    [Fact]
    public void BuildChatCompletionsUri_HandlesBaseAndV1Endpoints()
    {
        LocalReviewRunner.BuildChatCompletionsUri("http://127.0.0.1:62058")
            .Should().Be(new Uri("http://127.0.0.1:62058/v1/chat/completions"));
        LocalReviewRunner.BuildChatCompletionsUri("http://localhost:39839/v1")
            .Should().Be(new Uri("http://localhost:39839/v1/chat/completions"));
        LocalReviewRunner.BuildChatCompletionsUri(
                "http://localhost:39839/v1/chat/completions")
            .Should().Be(new Uri(
                "http://localhost:39839/v1/chat/completions"));
    }

    [Theory]
    [InlineData(32_768, 24_371)]
    [InlineData(40_960, 31_334)]
    [InlineData(131_072, 107_929)]
    public void ChunkBudget_ScalesWithModelContext(
        int contextLength,
        int expectedCharacters)
    {
        LocalReviewRunner.CalculateChunkCharacterBudget(contextLength)
            .Should().Be(expectedCharacters);
    }

    [Fact]
    public void PatchChunker_SplitsOnFileBoundariesWithinBudget()
    {
        var first = BuildFileDiff("src/a.cs", 7_000);
        var second = BuildFileDiff("src/b.cs", 7_000);
        var third = BuildFileDiff("src/c.cs", 7_000);

        var chunks = LocalReviewPatchChunker.Chunk(
            first + second + third,
            maxCharacters: 15_000);

        chunks.Should().HaveCount(2);
        chunks.Should().OnlyContain(chunk => chunk.Length <= 15_000);
        string.Concat(chunks).Should().Be(first + second + third);
        chunks[0].Should().Contain("src/a.cs").And.Contain("src/b.cs");
        chunks[1].Should().Contain("src/c.cs");
    }

    [Fact]
    public void PatchChunker_SplitsOversizedFileAndRepeatsItsHeader()
    {
        var patch = BuildFileDiff("src/large.cs", 30_000);

        var chunks = LocalReviewPatchChunker.Chunk(
            patch,
            maxCharacters: 10_000);

        chunks.Should().HaveCountGreaterThan(1);
        chunks.Should().OnlyContain(chunk => chunk.Length <= 10_000);
        chunks.Should().OnlyContain(chunk =>
            chunk.StartsWith("diff --git a/src/large.cs b/src/large.cs"));
    }

    [Fact]
    public void ExtractChatContent_ReadsOpenAiResponse()
    {
        var response = """
            {
              "choices": [
                {
                  "message": {
                    "role": "assistant",
                    "content": "{\"findings\":[]}"
                  }
                }
              ]
            }
            """;

        LocalReviewRunner.ExtractChatContent(response)
            .Should().Be("{\"findings\":[]}");
    }

    [Fact]
    public void ContextErrorParser_ReadsEffectiveRuntimeLimit()
    {
        var response = """
            {"error":{"message":"This request requires 16922 total tokens (14874 input + 2048 output), which exceeds the model's maximum context length of 8192 tokens."}}
            """;

        LocalReviewRunner.TryParseEffectiveContextLength(
            response,
            out var contextLength).Should().BeTrue();
        contextLength.Should().Be(8_192);
    }

    [Fact]
    public void ProviderErrorDetail_AddsWebGpuFallbackInstructions()
    {
        var detail = LocalReviewRunner.BuildProviderErrorDetail(
            "gpt-oss-20b",
            """{"error":{"message":"WebGPU validation failed. binding index 4 not present"}}""");

        detail.Should().Contain("WebGPU variant is incompatible");
        detail.Should().Contain(
            "foundry model download gpt-oss-20b-generic-cpu");
        detail.Should().Contain(
            "set the local reviewer model to gpt-oss-20b-generic-cpu");
    }

    [Fact]
    public void ProviderErrorDetail_ExplainsCpuAllocationFailure()
    {
        var detail = LocalReviewRunner.BuildProviderErrorDetail(
            "gpt-oss-20b-generic-cpu",
            """
            {"error":{"message":"BFCArena::AllocateRawInternal Failed to allocate memory for requested buffer of size 64434643968"}}
            """);

        detail.Should().Contain("exceeded available system memory");
        detail.Should().Contain("60.0 GiB buffer");
        detail.Should().Contain("qwen2.5-coder-7b NPU");
    }

    [Fact]
    public void ResponseParser_AcceptsFencedJsonAndDropsMalformedFindings()
    {
        var response = """
            ```json
            {
              "findings": [
                {
                  "severity": "high",
                  "confidence": "high",
                  "file": "src/example.cs",
                  "line": 42,
                  "title": "Null value reaches dereference",
                  "body": "The new branch permits null and dereferences it."
                },
                {
                  "severity": "medium",
                  "file": "",
                  "title": "Malformed"
                }
              ]
            }
            ```
            """;

        var parsed = LocalReviewResponseParser.Parse(
            response, "qwen2.5-coder-7b");

        parsed.Findings.Should().ContainSingle();
        parsed.Findings[0].Should().BeEquivalentTo(new Finding
        {
            Id = "local-01",
            Severity = FindingSeverity.High,
            Confidence = FindingConfidence.High,
            FoundBy = new[] { "qwen2.5-coder-7b" },
            File = "src/example.cs",
            Line = 42,
            DiffAnchorable = true,
            Title = "Null value reaches dereference",
            Body = "The new branch permits null and dereferences it.",
        });
        parsed.Warnings.Should().ContainSingle()
            .Which.Should().Contain("Ignored malformed local finding #2");
    }

    [Fact]
    public void ResponseParser_AcceptsCommonLocalModelAliases()
    {
        var response = """
            {
              "findings": [
                {
                  "issue": "Potential null dereference",
                  "description": "Calling Trim on a null input throws.",
                  "severity": "High",
                  "location": "a.cs",
                  "line": 1
                }
              ]
            }
            """;

        var parsed = LocalReviewResponseParser.Parse(
            response, "qwen2.5-coder-7b");

        parsed.Warnings.Should().BeEmpty();
        parsed.Findings.Should().ContainSingle();
        parsed.Findings[0].Title.Should().Be("Potential null dereference");
        parsed.Findings[0].Body.Should().Be(
            "Calling Trim on a null input throws.");
        parsed.Findings[0].File.Should().Be("a.cs");
        parsed.Findings[0].Severity.Should().Be(FindingSeverity.High);
        parsed.Findings[0].Confidence.Should().Be(FindingConfidence.Medium);
    }

    [Fact]
    public void ReviewRunStore_RehydratesLocalArtifact()
    {
        var runDirectory = Path.Combine(
            Path.GetTempPath(), $"pr-inbox-local-review-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runDirectory);
        try
        {
            var artifact = new LocalReviewArtifact
            {
                Status = LocalReviewStatus.Completed,
                Model = "qwen2.5-coder-7b",
                HeadSha = "abcdef1",
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                Findings = Array.Empty<Finding>(),
            };
            File.WriteAllText(
                Path.Combine(runDirectory, LocalReviewArtifact.FileName),
                JsonSerializer.Serialize(
                    artifact, LocalReviewArtifact.JsonOptions));

            var store = new ReviewRunStore();
            store.StartedRun(new ReviewRun(
                RunId: 1,
                PrUrl: "https://github.com/owner/repo/pull/1",
                RunDirectory: runDirectory,
                HeadSha: "abcdef1",
                StartedAtUtc: DateTimeOffset.UtcNow,
                FindingsAtUtc: null,
                Findings: null,
                FindingsErrors: Array.Empty<string>()));

            store.Get("https://github.com/owner/repo/pull/1")!
                .LocalReview.Should().BeEquivalentTo(artifact);
        }
        finally
        {
            Directory.Delete(runDirectory, recursive: true);
        }
    }

    [Fact]
    public void ReviewRunStore_IgnoresLocalResultFromSupersededRun()
    {
        var store = new ReviewRunStore();
        const string url = "https://github.com/owner/repo/pull/1";
        store.StartedRun(new ReviewRun(
            RunId: 2,
            PrUrl: url,
            RunDirectory: @"D:\reviews\new",
            HeadSha: "abcdef2",
            StartedAtUtc: DateTimeOffset.UtcNow,
            FindingsAtUtc: null,
            Findings: null,
            FindingsErrors: Array.Empty<string>()));

        store.UpdateLocalReview(
            url,
            @"D:\reviews\old",
            new LocalReviewArtifact
            {
                Status = LocalReviewStatus.Completed,
                Model = "qwen2.5-coder-7b",
                HeadSha = "abcdef1",
                GeneratedAtUtc = DateTimeOffset.UtcNow,
            });

        store.Get(url)!.LocalReview.Should().BeNull();
    }

    [Fact]
    public async Task Runner_WritesCompletedArtifactWithoutChangingAuthoritativeFindings()
    {
        await using var fixture = await RunnerFixture.CreateAsync(
            HttpStatusCode.OK,
            """
            {
              "choices": [
                {
                  "message": {
                    "content": "{\"findings\":[{\"issue\":\"Null dereference\",\"description\":\"Trim throws for null.\",\"severity\":\"high\",\"location\":\"src/a.cs\",\"line\":2}]}"
                  }
                }
              ]
            }
            """);

        await fixture.Runner.RunAsync(fixture.Brief, CancellationToken.None);

        var run = fixture.Store.Get(fixture.Brief.PrUrl)!;
        run.Findings.Should().BeNull(
            "the local shadow result must not replace findings.yaml");
        run.LocalReview.Should().NotBeNull();
        run.LocalReview!.Status.Should().Be(LocalReviewStatus.Completed);
        run.LocalReview.Findings.Should().ContainSingle()
            .Which.Title.Should().Be("Null dereference");
        File.Exists(Path.Combine(
            fixture.Brief.RunDirectory,
            LocalReviewArtifact.FileName)).Should().BeTrue();

        fixture.Handler.RequestBody.Should().Contain("return input.Trim()");
        fixture.Handler.RequestBody.Should()
            .NotContain("triage the existing review threads");
    }

    [Fact]
    public async Task Runner_PersistsFailureWithoutThrowingOrChangingAuthoritativeFindings()
    {
        await using var fixture = await RunnerFixture.CreateAsync(
            HttpStatusCode.InternalServerError,
            """{"error":{"message":"model failed"}}""");

        var act = () => fixture.Runner.RunAsync(
            fixture.Brief, CancellationToken.None);

        await act.Should().NotThrowAsync();
        var run = fixture.Store.Get(fixture.Brief.PrUrl)!;
        run.Findings.Should().BeNull();
        run.LocalReview!.Status.Should().Be(LocalReviewStatus.Failed);
        run.LocalReview.Error.Should().Contain("Local model request failed");
    }

    [Fact]
    public async Task Runner_ChunksLargePatchAndAggregatesFindings()
    {
        var patch = BuildFileDiff("src/a.cs", 15_000)
            + BuildFileDiff("src/b.cs", 15_000);
        await using var fixture = await RunnerFixture.CreateAsync(
            [
                new StubResponse(
                    HttpStatusCode.OK,
                    ChatResponse(
                        "First bug",
                        "src/a.cs",
                        10)),
                new StubResponse(
                    HttpStatusCode.OK,
                    ChatResponse(
                        "Second bug",
                        "src/b.cs",
                        20)),
            ],
            patch,
            contextLength: 24_576);

        await fixture.Runner.RunAsync(fixture.Brief, CancellationToken.None);

        fixture.Handler.RequestBodies.Should().HaveCount(2);
        var run = fixture.Store.Get(fixture.Brief.PrUrl)!;
        run.LocalReview!.Status.Should().Be(LocalReviewStatus.Completed);
        run.LocalReview.Findings.Select(finding => finding.Title)
            .Should().Equal("First bug", "Second bug");
        run.LocalReview.Findings.Select(finding => finding.Id)
            .Should().Equal("local-01", "local-02");
        run.LocalReview.Warnings.Should().ContainSingle(
            warning => warning.Contains("2 chunks"));
    }

    [Fact]
    public async Task Runner_RechunksWhenRuntimeLimitIsLowerThanCatalog()
    {
        var patch = BuildFileDiff("src/large.cs", 15_000);
        var contextError = """
            {"error":{"message":"Failed to handle OpenAI completion: This request requires 16922 total tokens (14874 input + 2048 output), which exceeds the model's maximum context length of 8192 tokens."}}
            """;
        await using var fixture = await RunnerFixture.CreateAsync(
            [
                new StubResponse(HttpStatusCode.BadRequest, contextError),
                new StubResponse(
                    HttpStatusCode.OK,
                    ChatResponse("First chunk bug", "src/large.cs", 10)),
                new StubResponse(
                    HttpStatusCode.OK,
                    ChatResponse("Second chunk bug", "src/large.cs", 20)),
            ],
            patch,
            contextLength: 131_072);

        await fixture.Runner.RunAsync(fixture.Brief, CancellationToken.None);

        fixture.Handler.RequestBodies.Should().HaveCount(3);
        var run = fixture.Store.Get(fixture.Brief.PrUrl)!;
        run.LocalReview!.Status.Should().Be(LocalReviewStatus.Completed);
        run.LocalReview.Findings.Select(finding => finding.Title)
            .Should().Equal("First chunk bug", "Second chunk bug");
        run.LocalReview.Warnings.Should().Contain(
            warning => warning.Contains("catalog reported 131,072")
                       && warning.Contains("runtime enforced 8,192"));
        run.LocalReview.Warnings.Should().Contain(
            warning => warning.Contains("2 chunks")
                       && warning.Contains("8,192-token"));
    }

    private sealed class RunnerFixture : IAsyncDisposable
    {
        private readonly Microsoft.Data.Sqlite.SqliteConnection _keepAlive;
        private readonly string _runDirectory;

        private RunnerFixture(
            Microsoft.Data.Sqlite.SqliteConnection keepAlive,
            string runDirectory,
            LocalReviewRunner runner,
            ReviewRunStore store,
            BriefResult brief,
            StubHttpHandler handler)
        {
            _keepAlive = keepAlive;
            _runDirectory = runDirectory;
            Runner = runner;
            Store = store;
            Brief = brief;
            Handler = handler;
        }

        public LocalReviewRunner Runner { get; }
        public ReviewRunStore Store { get; }
        public BriefResult Brief { get; }
        public StubHttpHandler Handler { get; }

        public static async Task<RunnerFixture> CreateAsync(
            HttpStatusCode status,
            string response)
            => await CreateAsync(
                [new StubResponse(status, response)],
                StubPatchProvider.DefaultPatch,
                contextLength: 32_768);

        public static async Task<RunnerFixture> CreateAsync(
            IReadOnlyList<StubResponse> responses,
            string patch,
            int contextLength)
        {
            var connString = PrInboxDb.InMemoryConnectionString(
                $"local-review-{Guid.NewGuid():N}");
            var db = new PrInboxDb(connString);
            var keepAlive = await db.OpenAsync();
            await new MigrationRunner().MigrateAsync(connString);

            var url = "https://github.com/owner/repo/pull/7";
            var prRepo = new PullRequestRepository(db);
            await prRepo.UpsertAsync(new PullRequestRow(
                    Identity: new PrIdentity(url, "gh.com:1#7"),
                    SourceKind: SourceKind.GitHub,
                    SourceId: "gh.com",
                    DisplayRepo: "owner/repo",
                    Number: 7,
                    Title: "Exercise local review",
                    AuthorLogin: "author",
                    Url: url,
                    Status: PullRequestStatus.Open,
                    TrackingReason: TrackingReason.Assigned,
                    IdentityUsed: "owner",
                    FirstSeenAt: DateTimeOffset.UtcNow,
                    LastSyncedAt: DateTimeOffset.UtcNow,
                    EnrichState: EnrichState.Enriched,
                    LastBriefedHeadSha: null,
                    LastReviewRunHeadSha: null,
                    LastPostedReviewHeadSha: null),
                CancellationToken.None);

            var runDirectory = Path.Combine(
                Path.GetTempPath(),
                $"pr-inbox-local-runner-{Guid.NewGuid():N}");
            Directory.CreateDirectory(runDirectory);
            var brief = new BriefResult(
                RunId: 1,
                RunDirectory: runDirectory,
                BriefPath: Path.Combine(runDirectory, "brief.md"),
                MetadataPath: Path.Combine(runDirectory, "metadata.json"),
                HeadSha: "abcdef1234567",
                PrUrl: url);

            var store = new ReviewRunStore();
            store.StartedRun(new ReviewRun(
                RunId: brief.RunId,
                PrUrl: brief.PrUrl,
                RunDirectory: brief.RunDirectory,
                HeadSha: brief.HeadSha,
                StartedAtUtc: DateTimeOffset.UtcNow,
                FindingsAtUtc: null,
                Findings: null,
                FindingsErrors: Array.Empty<string>()));

            var handler = new StubHttpHandler(responses);
            var config = new PrInboxConfig();
            config.LocalReviewer.Enabled = true;
            config.LocalReviewer.Endpoint = "http://127.0.0.1:39839";
            config.LocalReviewer.Model = "qwen2.5-coder-7b";

            var runner = new LocalReviewRunner(
                config,
                prRepo,
                new StubPatchProvider(patch),
                new StubFoundryRuntime(contextLength),
                new StubEndpointResolver(),
                new StubHttpClientFactory(handler),
                store,
                NullLogger<LocalReviewRunner>.Instance);

            return new RunnerFixture(
                keepAlive, runDirectory, runner, store, brief, handler);
        }

        public async ValueTask DisposeAsync()
        {
            await _keepAlive.DisposeAsync();
            Directory.Delete(_runDirectory, recursive: true);
        }
    }

    private sealed class StubPatchProvider : IReviewPatchProvider
    {
        public const string DefaultPatch = """
            --- a/src/a.cs
            +++ b/src/a.cs
            @@ -1 +1 @@
            -return input ?? string.Empty;
            +return input.Trim();
            """;

        private readonly string _patch;

        public StubPatchProvider(string patch)
        {
            _patch = patch;
        }

        public Task<ReviewPatchResult> GetPatchAsync(
            PullRequestRow pr,
            int maxCharacters,
            CancellationToken ct) =>
            Task.FromResult(ReviewPatchResult.Success(_patch));
    }

    private sealed class StubEndpointResolver : ILocalModelEndpointResolver
    {
        public Task<string> ResolveAsync(
            string configuredEndpoint,
            CancellationToken ct) =>
            Task.FromResult("http://127.0.0.1:39839");
    }

    private sealed class StubFoundryRuntime : IFoundryLocalRuntime
    {
        private readonly int _contextLength;

        public StubFoundryRuntime(int contextLength)
        {
            _contextLength = contextLength;
        }

        public Task<LocalModelRuntimeInfo> PrepareAsync(
            string configuredEndpoint,
            string model,
            int timeoutSeconds,
            CancellationToken ct) =>
            Task.FromResult(new LocalModelRuntimeInfo(_contextLength));
    }

    private sealed class StubFoundryCliRunner : IFoundryCliRunner
    {
        private readonly Queue<FoundryCliResult> _results;

        public StubFoundryCliRunner(params FoundryCliResult[] results)
        {
            _results = new Queue<FoundryCliResult>(results);
        }

        public List<string> Commands { get; } = new();

        public Task<FoundryCliResult> RunAsync(
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken ct)
        {
            Commands.Add(string.Join(' ', arguments));
            return Task.FromResult(_results.Dequeue());
        }
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

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Queue<StubResponse> _responses;

        public StubHttpHandler(IReadOnlyList<StubResponse> responses)
        {
            _responses = new Queue<StubResponse>(responses);
        }

        public IReadOnlyList<string> RequestBodies => _requestBodies;
        public string RequestBody => _requestBodies.LastOrDefault() ?? string.Empty;
        private readonly List<string> _requestBodies = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _requestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            var response = _responses.Dequeue();
            return new HttpResponseMessage(response.Status)
            {
                Content = new StringContent(
                    response.Body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record StubResponse(HttpStatusCode Status, string Body);

    private static string BuildFileDiff(string path, int bodyCharacters)
    {
        var header = $"""
            diff --git a/{path} b/{path}
            index 1111111..2222222 100644
            --- a/{path}
            +++ b/{path}
            @@ -1,1 +1,1 @@
            """;
        return header + "\n+" + new string('x', bodyCharacters) + "\n";
    }

    private static string ChatResponse(string title, string file, int line)
    {
        var content = JsonSerializer.Serialize(new
        {
            findings = new[]
            {
                new
                {
                    severity = "medium",
                    confidence = "high",
                    file,
                    line,
                    title,
                    body = "Concrete bug.",
                },
            },
        });
        return JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content,
                    },
                },
            },
        });
    }
}
