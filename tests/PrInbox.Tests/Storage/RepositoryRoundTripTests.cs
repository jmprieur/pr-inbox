using PrInbox.Core.Models;
using PrInbox.Core.Storage;

namespace PrInbox.Tests.Storage;

/// <summary>
/// Round-trip tests for the repository layer. Uses an in-memory SQLite
/// database with a held-open connection so the shared cache persists for
/// the lifetime of the test.
/// </summary>
public class RepositoryRoundTripTests : IAsyncLifetime
{
    private string _connString = string.Empty;
    private PrInboxDb _db = null!;
    private Microsoft.Data.Sqlite.SqliteConnection _keepAlive = null!;

    public async Task InitializeAsync()
    {
        _connString = PrInboxDb.InMemoryConnectionString($"repo-{Guid.NewGuid():N}");
        _db = new PrInboxDb(_connString);
        _keepAlive = await _db.OpenAsync();
        await new MigrationRunner().MigrateAsync(_connString);
    }

    public async Task DisposeAsync()
    {
        await _keepAlive.DisposeAsync();
    }

    private static PullRequestRow SampleRow(PrIdentity? id = null)
    {
        id ??= new PrIdentity(
            Url: "https://github.com/agency-microsoft/playground/pull/4248",
            Stable: "gh.com:100#4248");

        return new PullRequestRow(
            Identity: id.Value,
            SourceKind: SourceKind.GitHub,
            SourceId: "gh.com",
            DisplayRepo: "agency-microsoft/playground",
            Number: 4248,
            Title: "Sample PR",
            AuthorLogin: "octocat",
            Url: "https://github.com/agency-microsoft/playground/pull/4248",
            Status: PullRequestStatus.Open,
            TrackingReason: TrackingReason.Assigned,
            IdentityUsed: "jmprieur_public",
            FirstSeenAt: DateTimeOffset.Parse("2026-05-13T20:00:00Z"),
            LastSyncedAt: DateTimeOffset.Parse("2026-05-13T20:30:00Z"),
            EnrichState: EnrichState.Basic,
            LastBriefedHeadSha: null,
            LastReviewRunHeadSha: null,
            LastPostedReviewHeadSha: null);
    }

    [Fact]
    public async Task PullRequest_Upsert_Then_Get_Returns_Same_Row()
    {
        var repo = new PullRequestRepository(_db);
        var row = SampleRow();

        await repo.UpsertAsync(row, CancellationToken.None);
        var fetched = await repo.GetAsync(row.Identity.Url, CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.Identity.Should().Be(row.Identity);
        fetched.Title.Should().Be("Sample PR");
        fetched.Status.Should().Be(PullRequestStatus.Open);
        fetched.TrackingReason.Should().Be(TrackingReason.Assigned);
    }

    [Fact]
    public async Task PullRequest_Upsert_Twice_Updates_Mutable_Fields()
    {
        var repo = new PullRequestRepository(_db);
        var first = SampleRow();
        await repo.UpsertAsync(first, CancellationToken.None);

        var second = first with
        {
            Title = "Sample PR (renamed)",
            LastSyncedAt = DateTimeOffset.Parse("2026-05-14T08:00:00Z"),
        };
        await repo.UpsertAsync(second, CancellationToken.None);

        var fetched = await repo.GetAsync(first.Identity.Url, CancellationToken.None);
        fetched.Should().NotBeNull();
        fetched!.Title.Should().Be("Sample PR (renamed)");
        fetched.LastSyncedAt.Should().Be(DateTimeOffset.Parse("2026-05-14T08:00:00Z"));
    }

    [Fact]
    public async Task MarkPreviouslyAssigned_Only_Updates_When_Was_Assigned()
    {
        var repo = new PullRequestRepository(_db);
        var row = SampleRow();
        await repo.UpsertAsync(row, CancellationToken.None);

        await repo.MarkPreviouslyAssignedAsync(row.Identity.Url, CancellationToken.None);

        var fetched = await repo.GetAsync(row.Identity.Url, CancellationToken.None);
        fetched!.TrackingReason.Should().Be(TrackingReason.PreviouslyAssigned);
    }

    [Fact]
    public async Task ListActive_Excludes_Closed_And_Archived()
    {
        var repo = new PullRequestRepository(_db);

        await repo.UpsertAsync(SampleRow(new PrIdentity("https://github.com/o/r/pull/1", "gh.com:1#1")), CancellationToken.None);
        await repo.UpsertAsync(SampleRow(new PrIdentity("https://github.com/o/r/pull/2", "gh.com:1#2"))
            with { Status = PullRequestStatus.Closed }, CancellationToken.None);
        await repo.UpsertAsync(SampleRow(new PrIdentity("https://github.com/o/r/pull/3", "gh.com:1#3"))
            with { TrackingReason = TrackingReason.Archived }, CancellationToken.None);

        var active = await repo.ListActiveAsync(CancellationToken.None);
        active.Should().ContainSingle(p => p.Identity.Url == "https://github.com/o/r/pull/1");
    }

    [Fact]
    public async Task PrSnapshot_Dedupes_Identical_Snapshots()
    {
        var prRepo = new PullRequestRepository(_db);
        var snapRepo = new PrSnapshotRepository(_db);
        var row = SampleRow();
        await prRepo.UpsertAsync(row, CancellationToken.None);

        var inserted1 = await snapRepo.InsertIfChangedAsync(
            row.Identity, DateTimeOffset.UtcNow,
            headSha: "abc",
            baseSha: "base",
            mergeBaseSha: null,
            orderedCommitShas: new[] { "abc" },
            reviewerState: ReviewerState.Requested,
            prState: PullRequestStatus.Open,
            rawMetadataJson: null,
            CancellationToken.None);

        var inserted2 = await snapRepo.InsertIfChangedAsync(
            row.Identity, DateTimeOffset.UtcNow.AddMinutes(1),
            headSha: "abc",
            baseSha: "base",
            mergeBaseSha: null,
            orderedCommitShas: new[] { "abc" },
            reviewerState: ReviewerState.Requested,
            prState: PullRequestStatus.Open,
            rawMetadataJson: null,
            CancellationToken.None);

        inserted1.Should().BeTrue();
        inserted2.Should().BeFalse();
    }

    [Fact]
    public async Task PrSnapshot_Inserts_New_Row_When_Head_Changes()
    {
        var prRepo = new PullRequestRepository(_db);
        var snapRepo = new PrSnapshotRepository(_db);
        var row = SampleRow();
        await prRepo.UpsertAsync(row, CancellationToken.None);

        await snapRepo.InsertIfChangedAsync(
            row.Identity, DateTimeOffset.UtcNow,
            "abc", "base", null,
            new[] { "abc" }, ReviewerState.Requested, PullRequestStatus.Open, null,
            CancellationToken.None);

        var inserted = await snapRepo.InsertIfChangedAsync(
            row.Identity, DateTimeOffset.UtcNow.AddMinutes(5),
            "def", "base", null,
            new[] { "def", "abc" }, ReviewerState.Requested, PullRequestStatus.Open, null,
            CancellationToken.None);

        inserted.Should().BeTrue();
        var latest = await snapRepo.GetLatestAsync(row.Identity, CancellationToken.None);
        latest!.HeadSha.Should().Be("def");
        latest.OrderedCommitShas.Should().HaveCount(2);
    }

    [Fact]
    public async Task ObservedThread_Upsert_Preserves_FirstSeen_And_Moves_LastSeen()
    {
        var prRepo = new PullRequestRepository(_db);
        var threadRepo = new ObservedThreadRepository(_db);
        var row = SampleRow();
        await prRepo.UpsertAsync(row, CancellationToken.None);

        var t1 = DateTimeOffset.Parse("2026-05-13T10:00:00Z");
        var t2 = DateTimeOffset.Parse("2026-05-14T10:00:00Z");

        var thread = new RemoteThread(
            PlatformThreadId: "thread-1",
            Kind: ThreadKind.ReviewComment,
            AuthorLogin: "Copilot",
            IsBot: true,
            BotKind: BotKind.CopilotReview,
            IsResolved: false,
            CreatedAt: t1,
            LastUpdatedAt: t1,
            RawJson: "{}");

        await threadRepo.UpsertManyAsync(row.Identity, new[] { thread }, syncedAt: t1, CancellationToken.None);
        await threadRepo.UpsertManyAsync(row.Identity, new[] { thread }, syncedAt: t2, CancellationToken.None);

        var open = await threadRepo.GetOpenThreadsAsync(row.Identity, CancellationToken.None);
        open.Should().ContainSingle();
        open[0].FirstSeenAt.Should().Be(t1);
        open[0].LastSeenAt.Should().Be(t2);
    }

    [Fact]
    public async Task ObservedThread_Upsert_Sets_ResolvedAt_When_Newly_Resolved()
    {
        var prRepo = new PullRequestRepository(_db);
        var threadRepo = new ObservedThreadRepository(_db);
        var row = SampleRow();
        await prRepo.UpsertAsync(row, CancellationToken.None);

        var t1 = DateTimeOffset.Parse("2026-05-13T10:00:00Z");
        var t2 = DateTimeOffset.Parse("2026-05-14T10:00:00Z");

        var openThread = new RemoteThread(
            PlatformThreadId: "thread-1",
            Kind: ThreadKind.ReviewComment,
            AuthorLogin: "jmprieur",
            IsBot: false,
            BotKind: null,
            IsResolved: false,
            CreatedAt: t1,
            LastUpdatedAt: t1,
            RawJson: "{}");

        var resolvedThread = openThread with { IsResolved = true };

        await threadRepo.UpsertManyAsync(row.Identity, new[] { openThread }, syncedAt: t1, CancellationToken.None);

        var openCount1 = (await threadRepo.GetOpenThreadsAsync(row.Identity, CancellationToken.None)).Count;
        openCount1.Should().Be(1);

        await threadRepo.UpsertManyAsync(row.Identity, new[] { resolvedThread }, syncedAt: t2, CancellationToken.None);

        var openCount2 = (await threadRepo.GetOpenThreadsAsync(row.Identity, CancellationToken.None)).Count;
        openCount2.Should().Be(0);
    }

    [Fact]
    public async Task SyncRun_Start_Then_Complete_Records_Correctly()
    {
        var repo = new SyncRunRepository(_db);
        var id = await repo.StartAsync("gh.com", "jmprieur_public", CancellationToken.None);
        await repo.CompleteAsync(id, SyncRunStatus.Ok, prsSeen: 5, error: null, CancellationToken.None);

        var latest = await repo.GetLatestPerSourceAsync(CancellationToken.None);
        latest.Should().ContainSingle();
        latest[0].Status.Should().Be(SyncRunStatus.Ok);
        latest[0].PrsSeen.Should().Be(5);
        latest[0].CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ReviewRun_Insert_Then_List_Returns_Newest_First()
    {
        var prRepo = new PullRequestRepository(_db);
        var reviewRepo = new ReviewRunRepository(_db);
        var row = SampleRow();
        await prRepo.UpsertAsync(row, CancellationToken.None);

        await reviewRepo.InsertAsync(row.Identity,
            createdAt: DateTimeOffset.Parse("2026-05-13T10:00:00Z"),
            briefPath: @"C:\reviews\one\brief.md",
            runDirectory: @"C:\reviews\one",
            headSha: "abc",
            baseSha: "base",
            status: ReviewRunStatus.Generated,
            notes: null,
            CancellationToken.None);

        await reviewRepo.InsertAsync(row.Identity,
            createdAt: DateTimeOffset.Parse("2026-05-14T10:00:00Z"),
            briefPath: @"C:\reviews\two\brief.md",
            runDirectory: @"C:\reviews\two",
            headSha: "def",
            baseSha: "base",
            status: ReviewRunStatus.Generated,
            notes: null,
            CancellationToken.None);

        var runs = await reviewRepo.ListForPrAsync(row.Identity, CancellationToken.None);
        runs.Should().HaveCount(2);
        runs[0].HeadSha.Should().Be("def");
        runs[1].HeadSha.Should().Be("abc");
    }

    [Fact]
    public async Task PullRequest_Upsert_Records_Source_Binding_Idempotently()
    {
        // Two different (source, identity) pairs discover the same PR URL.
        // Expected: one row in pull_requests, one binding per discovery.
        var repo = new PullRequestRepository(_db);
        var url = "https://github.com/owner/repo/pull/42";
        var stable = "gh.com:5000#42";

        var rowEmu = SampleRow(new PrIdentity(url, stable)) with
        {
            SourceId = "gh.com:emu",
            IdentityUsed = "jmprieur_microsoft",
        };
        var rowPublic = SampleRow(new PrIdentity(url, stable)) with
        {
            SourceId = "gh.com:public",
            IdentityUsed = "jmprieur",
            LastSyncedAt = DateTimeOffset.Parse("2026-05-14T08:00:00Z"),
        };

        await repo.UpsertAsync(rowEmu, CancellationToken.None);
        await repo.UpsertAsync(rowPublic, CancellationToken.None);

        // Same upsert twice with same (source, identity) does NOT duplicate bindings.
        await repo.UpsertAsync(rowEmu, CancellationToken.None);

        await using var conn = await _db.OpenAsync(CancellationToken.None);
        await using var bindingsCmd = conn.CreateCommand();
        bindingsCmd.CommandText = """
            SELECT source_id, identity_used FROM pr_source_bindings
            WHERE pr_identity = $url ORDER BY source_id;
            """;
        bindingsCmd.Parameters.AddWithValue("$url", url);
        var bindings = new List<(string SourceId, string IdentityUsed)>();
        await using var reader = await bindingsCmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            bindings.Add((reader.GetString(0), reader.GetString(1)));
        }
        bindings.Should().HaveCount(2);
        bindings.Should().Contain(("gh.com:emu", "jmprieur_microsoft"));
        bindings.Should().Contain(("gh.com:public", "jmprieur"));

        // pull_requests still has exactly one row keyed by URL.
        await using var prCmd = conn.CreateCommand();
        prCmd.CommandText = "SELECT COUNT(*) FROM pull_requests WHERE pr_identity = $url;";
        prCmd.Parameters.AddWithValue("$url", url);
        var count = Convert.ToInt64(await prCmd.ExecuteScalarAsync());
        count.Should().Be(1L);
    }

    [Fact]
    public async Task PullRequest_Upsert_Round_Trips_EnrichState()
    {
        var repo = new PullRequestRepository(_db);
        var enrichedRow = SampleRow() with { EnrichState = EnrichState.Enriched };

        await repo.UpsertAsync(enrichedRow, CancellationToken.None);
        var fetched = await repo.GetAsync(enrichedRow.Identity.Url, CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.EnrichState.Should().Be(EnrichState.Enriched);
    }

    [Fact]
    public async Task MarkEnrichedAsync_Promotes_Row_From_Basic_To_Enriched()
    {
        var repo = new PullRequestRepository(_db);
        var basicRow = SampleRow() with { EnrichState = EnrichState.Basic };
        await repo.UpsertAsync(basicRow, CancellationToken.None);

        await repo.MarkEnrichedAsync(basicRow.Identity.Url, CancellationToken.None);

        var fetched = await repo.GetAsync(basicRow.Identity.Url, CancellationToken.None);
        fetched!.EnrichState.Should().Be(EnrichState.Enriched);
    }

    [Fact]
    public async Task ListNeedingEnrichment_Scopes_By_Source_And_Identity_And_Excludes_Enriched_And_Stale()
    {
        var repo = new PullRequestRepository(_db);

        // PR 1: open + assigned + basic + bound to (emu, jmprieur_microsoft) — INCLUDED
        var rowEmuBasic = SampleRow(new PrIdentity("https://github.com/o/r/pull/1", "gh.com:1#1")) with
        {
            SourceId = "gh.com:emu",
            IdentityUsed = "jmprieur_microsoft",
            EnrichState = EnrichState.Basic,
            Status = PullRequestStatus.Open,
            TrackingReason = TrackingReason.Assigned,
        };
        await repo.UpsertAsync(rowEmuBasic, CancellationToken.None);

        // PR 2: same source+identity but already 'enriched' — EXCLUDED
        var rowEmuEnriched = SampleRow(new PrIdentity("https://github.com/o/r/pull/2", "gh.com:1#2")) with
        {
            SourceId = "gh.com:emu",
            IdentityUsed = "jmprieur_microsoft",
            EnrichState = EnrichState.Enriched,
            Status = PullRequestStatus.Open,
            TrackingReason = TrackingReason.Assigned,
        };
        await repo.UpsertAsync(rowEmuEnriched, CancellationToken.None);

        // PR 3: basic but bound to a different identity — EXCLUDED for EMU caller
        var rowPublicBasic = SampleRow(new PrIdentity("https://github.com/o/r/pull/3", "gh.com:1#3")) with
        {
            SourceId = "gh.com:public",
            IdentityUsed = "jmprieur",
            EnrichState = EnrichState.Basic,
            Status = PullRequestStatus.Open,
            TrackingReason = TrackingReason.Assigned,
        };
        await repo.UpsertAsync(rowPublicBasic, CancellationToken.None);

        // PR 4: basic + correct binding but closed — EXCLUDED
        var rowEmuClosed = SampleRow(new PrIdentity("https://github.com/o/r/pull/4", "gh.com:1#4")) with
        {
            SourceId = "gh.com:emu",
            IdentityUsed = "jmprieur_microsoft",
            EnrichState = EnrichState.Basic,
            Status = PullRequestStatus.Closed,
            TrackingReason = TrackingReason.Assigned,
        };
        await repo.UpsertAsync(rowEmuClosed, CancellationToken.None);

        // PR 5: basic + correct binding but previously_assigned — EXCLUDED
        var rowEmuPrev = SampleRow(new PrIdentity("https://github.com/o/r/pull/5", "gh.com:1#5")) with
        {
            SourceId = "gh.com:emu",
            IdentityUsed = "jmprieur_microsoft",
            EnrichState = EnrichState.Basic,
            Status = PullRequestStatus.Open,
            TrackingReason = TrackingReason.PreviouslyAssigned,
        };
        await repo.UpsertAsync(rowEmuPrev, CancellationToken.None);

        var candidates = await repo.ListNeedingEnrichmentAsync(
            "gh.com:emu", "jmprieur_microsoft", CancellationToken.None);

        candidates.Should().ContainSingle();
        candidates[0].Identity.Url.Should().Be("https://github.com/o/r/pull/1");
    }
}

