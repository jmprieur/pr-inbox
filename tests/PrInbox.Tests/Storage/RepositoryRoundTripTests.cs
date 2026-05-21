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

        // PR 2: same source+identity, already enriched AND stamped at the
        // current dossier version — EXCLUDED (no backfill needed).
        var rowEmuEnriched = SampleRow(new PrIdentity("https://github.com/o/r/pull/2", "gh.com:1#2")) with
        {
            SourceId = "gh.com:emu",
            IdentityUsed = "jmprieur_microsoft",
            EnrichState = EnrichState.Enriched,
            Status = PullRequestStatus.Open,
            TrackingReason = TrackingReason.Assigned,
        };
        await repo.UpsertAsync(rowEmuEnriched, CancellationToken.None);
        await repo.UpdateDossierVersionAsync(
            rowEmuEnriched.Identity.Url,
            PrInbox.Core.Reviewing.BriefService.CurrentDossierVersion,
            CancellationToken.None);

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
            "gh.com:emu", "jmprieur_microsoft",
            minDossierVersion: PrInbox.Core.Reviewing.BriefService.CurrentDossierVersion,
            CancellationToken.None);

        candidates.Should().ContainSingle();
        candidates[0].Identity.Url.Should().Be("https://github.com/o/r/pull/1");
    }

    // -----------------------------------------------------------------
    // Brief dossier round-trip (post-v0.2 fields: body, files,
    // mergeable_state, ci_status, thread anchor/body).
    // -----------------------------------------------------------------

    [Fact]
    public async Task PullRequest_Body_RoundTrips_And_Preserved_Across_FastPass_Upsert()
    {
        var repo = new PullRequestRepository(_db);
        var withBody = SampleRow() with { Body = "## Summary\nFix the thing." };
        await repo.UpsertAsync(withBody, CancellationToken.None);

        // Fast-pass upsert (no body) should not wipe the enriched body.
        var withoutBody = SampleRow() with { Body = null, LastSyncedAt = DateTimeOffset.UtcNow };
        await repo.UpsertAsync(withoutBody, CancellationToken.None);

        var fetched = await repo.GetAsync(withBody.Identity.Url, CancellationToken.None);
        fetched!.Body.Should().Be("## Summary\nFix the thing.");
    }

    [Fact]
    public async Task PullRequest_UpdateBodyAsync_Sets_Body()
    {
        var repo = new PullRequestRepository(_db);
        var row = SampleRow();
        await repo.UpsertAsync(row, CancellationToken.None);

        await repo.UpdateBodyAsync(row.Identity.Url, "Updated body text.", CancellationToken.None);

        var fetched = await repo.GetAsync(row.Identity.Url, CancellationToken.None);
        fetched!.Body.Should().Be("Updated body text.");
    }

    [Fact]
    public async Task PrSnapshot_RoundTrips_Mergeable_Ci_Files()
    {
        var prRepo = new PullRequestRepository(_db);
        var snapRepo = new PrSnapshotRepository(_db);
        var row = SampleRow();
        await prRepo.UpsertAsync(row, CancellationToken.None);

        var files = new[]
        {
            new SnapshotFileChange("src/Foo.cs", 30, 5, "modified"),
            new SnapshotFileChange("README.md", 2, 0, "modified"),
        };

        var inserted = await snapRepo.InsertIfChangedAsync(
            row.Identity, DateTimeOffset.UtcNow,
            headSha: "abc", baseSha: "base", mergeBaseSha: null,
            orderedCommitShas: new[] { "abc" },
            reviewerState: ReviewerState.Requested,
            prState: PullRequestStatus.Open,
            rawMetadataJson: null,
            CancellationToken.None,
            mergeableState: "clean",
            ciStatus: "success",
            files: files);

        inserted.Should().BeTrue();
        var latest = await snapRepo.GetLatestAsync(row.Identity, CancellationToken.None);
        latest!.MergeableState.Should().Be("clean");
        latest.CiStatus.Should().Be("success");
        latest.Files.Should().NotBeNull();
        latest.Files!.Should().HaveCount(2);
        latest.Files![0].Path.Should().Be("src/Foo.cs");
        latest.Files![0].Additions.Should().Be(30);
        latest.Files![0].Deletions.Should().Be(5);
    }

    [Fact]
    public async Task PrSnapshot_CiOnly_Change_Does_Not_Insert_New_Snapshot()
    {
        var prRepo = new PullRequestRepository(_db);
        var snapRepo = new PrSnapshotRepository(_db);
        var row = SampleRow();
        await prRepo.UpsertAsync(row, CancellationToken.None);

        await snapRepo.InsertIfChangedAsync(
            row.Identity, DateTimeOffset.UtcNow,
            "abc", "base", null, new[] { "abc" },
            ReviewerState.Requested, PullRequestStatus.Open, null,
            CancellationToken.None, ciStatus: "pending");

        // Same canonical state; only CI changed pending → success.
        var inserted = await snapRepo.InsertIfChangedAsync(
            row.Identity, DateTimeOffset.UtcNow.AddMinutes(1),
            "abc", "base", null, new[] { "abc" },
            ReviewerState.Requested, PullRequestStatus.Open, null,
            CancellationToken.None, ciStatus: "success");

        inserted.Should().BeFalse();
    }

    [Fact]
    public async Task ObservedThread_RoundTrips_Anchor_And_Body()
    {
        var prRepo = new PullRequestRepository(_db);
        var threadRepo = new ObservedThreadRepository(_db);
        var row = SampleRow();
        await prRepo.UpsertAsync(row, CancellationToken.None);

        var t1 = DateTimeOffset.Parse("2026-05-13T10:00:00Z");
        var thread = new RemoteThread(
            PlatformThreadId: "thread-anchor-1",
            Kind: ThreadKind.ReviewComment,
            AuthorLogin: "Copilot",
            IsBot: true,
            BotKind: BotKind.CopilotReview,
            IsResolved: false,
            CreatedAt: t1,
            LastUpdatedAt: t1,
            RawJson: "{}",
            BodyExcerpt: "NuGet does NOT auto-update; …",
            AnchorPath: "src/Foo.cs",
            AnchorLine: 214);

        await threadRepo.UpsertManyAsync(row.Identity, new[] { thread }, syncedAt: t1, CancellationToken.None);

        var open = await threadRepo.GetOpenThreadsAsync(row.Identity, CancellationToken.None);
        open.Should().ContainSingle();
        open[0].LastCommentBody.Should().Be("NuGet does NOT auto-update; …");
        open[0].AnchorPath.Should().Be("src/Foo.cs");
        open[0].AnchorLine.Should().Be(214);
    }

    [Fact]
    public async Task ObservedThread_Body_Preserved_When_Subsequent_Upsert_Has_Null()
    {
        var prRepo = new PullRequestRepository(_db);
        var threadRepo = new ObservedThreadRepository(_db);
        var row = SampleRow();
        await prRepo.UpsertAsync(row, CancellationToken.None);

        var t1 = DateTimeOffset.Parse("2026-05-13T10:00:00Z");
        var t2 = DateTimeOffset.Parse("2026-05-14T10:00:00Z");

        var withBody = new RemoteThread(
            PlatformThreadId: "thread-body-1",
            Kind: ThreadKind.ReviewComment,
            AuthorLogin: "Copilot",
            IsBot: true,
            BotKind: BotKind.CopilotReview,
            IsResolved: false,
            CreatedAt: t1,
            LastUpdatedAt: t1,
            RawJson: "{}",
            BodyExcerpt: "Original body",
            AnchorPath: "src/Foo.cs",
            AnchorLine: 10);

        var withoutBody = withBody with { BodyExcerpt = null, AnchorPath = null, AnchorLine = null, LastUpdatedAt = t2 };

        await threadRepo.UpsertManyAsync(row.Identity, new[] { withBody }, syncedAt: t1, CancellationToken.None);
        await threadRepo.UpsertManyAsync(row.Identity, new[] { withoutBody }, syncedAt: t2, CancellationToken.None);

        var open = await threadRepo.GetOpenThreadsAsync(row.Identity, CancellationToken.None);
        open[0].LastCommentBody.Should().Be("Original body");
        open[0].AnchorPath.Should().Be("src/Foo.cs");
        open[0].AnchorLine.Should().Be(10);
    }

    // -----------------------------------------------------------------
    // Dossier-version backfill (migration 007).
    // -----------------------------------------------------------------

    [Fact]
    public async Task ListNeedingEnrichment_Includes_DossierBelowVersion_EvenIfEnriched()
    {
        var repo = new PullRequestRepository(_db);

        // PR 1: enriched but dossier_version stamped at 0 → re-elected for backfill.
        var legacy = SampleRow(new PrIdentity("https://github.com/o/r/pull/1", "gh.com:1#1")) with
        {
            SourceId = "gh.com",
            IdentityUsed = "jmprieur",
            EnrichState = EnrichState.Enriched,
            Status = PullRequestStatus.Open,
            TrackingReason = TrackingReason.Assigned,
        };
        await repo.UpsertAsync(legacy, CancellationToken.None);
        // (no UpdateDossierVersionAsync — leaves it at the SQL default 0)

        // PR 2: enriched and stamped at dossier_version=1 → excluded.
        var fresh = SampleRow(new PrIdentity("https://github.com/o/r/pull/2", "gh.com:1#2")) with
        {
            SourceId = "gh.com",
            IdentityUsed = "jmprieur",
            EnrichState = EnrichState.Enriched,
            Status = PullRequestStatus.Open,
            TrackingReason = TrackingReason.Assigned,
        };
        await repo.UpsertAsync(fresh, CancellationToken.None);
        await repo.UpdateDossierVersionAsync(fresh.Identity.Url, 1, CancellationToken.None);

        var candidates = await repo.ListNeedingEnrichmentAsync(
            "gh.com", "jmprieur",
            minDossierVersion: 1,
            CancellationToken.None);

        candidates.Should().ContainSingle();
        candidates[0].Identity.Url.Should().Be("https://github.com/o/r/pull/1");
    }

    [Fact]
    public async Task ListNeedingEnrichment_Skips_Ignored_Prs()
    {
        var repo = new PullRequestRepository(_db);
        var row = SampleRow() with
        {
            SourceId = "gh.com",
            IdentityUsed = "jmprieur",
            EnrichState = EnrichState.Basic,
            Status = PullRequestStatus.Open,
            TrackingReason = TrackingReason.Assigned,
        };
        await repo.UpsertAsync(row, CancellationToken.None);
        await repo.SetIgnoredAsync(row.Identity.Url, ignored: true, CancellationToken.None);

        var candidates = await repo.ListNeedingEnrichmentAsync(
            "gh.com", "jmprieur",
            minDossierVersion: 1,
            CancellationToken.None);

        candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateDossierVersionAsync_Stamps_Row()
    {
        var repo = new PullRequestRepository(_db);
        var row = SampleRow();
        await repo.UpsertAsync(row, CancellationToken.None);

        await repo.UpdateDossierVersionAsync(row.Identity.Url, 1, CancellationToken.None);

        var fetched = await repo.GetAsync(row.Identity.Url, CancellationToken.None);
        fetched!.DossierVersion.Should().Be(1);
    }

    [Fact]
    public async Task UpdateLatestDossierAsync_Updates_Latest_Snapshot_InPlace()
    {
        var prRepo = new PullRequestRepository(_db);
        var snapRepo = new PrSnapshotRepository(_db);
        var row = SampleRow();
        await prRepo.UpsertAsync(row, CancellationToken.None);

        // First enrich — no dossier metadata (legacy).
        await snapRepo.InsertIfChangedAsync(
            row.Identity, DateTimeOffset.UtcNow,
            "abc", "base", null, new[] { "abc" },
            ReviewerState.Requested, PullRequestStatus.Open, null,
            CancellationToken.None);

        // Backfill enrich — same canonical state (dedup blocks new insert),
        // but we want the dossier fields to populate.
        await snapRepo.UpdateLatestDossierAsync(
            row.Identity,
            mergeableState: "clean",
            ciStatus: "success",
            files: new[] { new SnapshotFileChange("src/A.cs", 10, 2, "modified") },
            CancellationToken.None);

        var latest = await snapRepo.GetLatestAsync(row.Identity, CancellationToken.None);
        latest!.MergeableState.Should().Be("clean");
        latest.CiStatus.Should().Be("success");
        latest.Files.Should().NotBeNull();
        latest.Files!.Should().ContainSingle();
    }

    [Fact]
    public async Task UpdateLatestDossierAsync_Preserves_Existing_Values_When_Passed_Null()
    {
        var prRepo = new PullRequestRepository(_db);
        var snapRepo = new PrSnapshotRepository(_db);
        var row = SampleRow();
        await prRepo.UpsertAsync(row, CancellationToken.None);

        await snapRepo.InsertIfChangedAsync(
            row.Identity, DateTimeOffset.UtcNow,
            "abc", "base", null, new[] { "abc" },
            ReviewerState.Requested, PullRequestStatus.Open, null,
            CancellationToken.None,
            mergeableState: "clean", ciStatus: "success");

        // Partial backfill — only files now, mergeable/ci unchanged.
        await snapRepo.UpdateLatestDossierAsync(
            row.Identity,
            mergeableState: null,
            ciStatus: null,
            files: new[] { new SnapshotFileChange("src/A.cs", 1, 0, "added") },
            CancellationToken.None);

        var latest = await snapRepo.GetLatestAsync(row.Identity, CancellationToken.None);
        latest!.MergeableState.Should().Be("clean"); // preserved
        latest.CiStatus.Should().Be("success"); // preserved
        latest.Files.Should().NotBeNull();
    }

    // -----------------------------------------------------------------
    // Phase 1 — thread node id (migration 008) + reopen semantics.
    // -----------------------------------------------------------------

    [Fact]
    public async Task ObservedThread_RoundTrips_PlatformThreadNodeId()
    {
        var prRepo = new PullRequestRepository(_db);
        var threadRepo = new ObservedThreadRepository(_db);
        var row = SampleRow();
        await prRepo.UpsertAsync(row, CancellationToken.None);

        var t1 = DateTimeOffset.Parse("2026-05-17T10:00:00Z");
        var thread = new RemoteThread(
            PlatformThreadId: "review-comment:42",
            Kind: ThreadKind.ReviewComment,
            AuthorLogin: "Copilot",
            IsBot: true,
            BotKind: PrInbox.Core.Models.BotKind.CopilotReview,
            IsResolved: false,
            CreatedAt: t1,
            LastUpdatedAt: t1,
            RawJson: "{}",
            BodyExcerpt: null,
            AnchorPath: null,
            AnchorLine: null,
            PlatformThreadNodeId: "PRRT_kwDOABCDEF");

        await threadRepo.UpsertManyAsync(row.Identity, new[] { thread }, syncedAt: t1, CancellationToken.None);

        var open = await threadRepo.GetOpenThreadsAsync(row.Identity, CancellationToken.None);
        open.Should().ContainSingle();
        open[0].PlatformThreadNodeId.Should().Be("PRRT_kwDOABCDEF");
    }

    [Fact]
    public async Task ObservedThread_PlatformThreadNodeId_Preserved_When_Subsequent_Upsert_Has_Null()
    {
        // Models the case where GraphQL was unavailable during a sync and
        // the REST-only path emits a thread with null node id. We must not
        // overwrite a value we previously learned.
        var prRepo = new PullRequestRepository(_db);
        var threadRepo = new ObservedThreadRepository(_db);
        var row = SampleRow();
        await prRepo.UpsertAsync(row, CancellationToken.None);

        var t1 = DateTimeOffset.Parse("2026-05-17T10:00:00Z");
        var t2 = DateTimeOffset.Parse("2026-05-17T11:00:00Z");

        var withNode = new RemoteThread(
            PlatformThreadId: "review-comment:42",
            Kind: ThreadKind.ReviewComment,
            AuthorLogin: "Copilot", IsBot: true, BotKind: PrInbox.Core.Models.BotKind.CopilotReview,
            IsResolved: false,
            CreatedAt: t1, LastUpdatedAt: t1, RawJson: "{}",
            BodyExcerpt: null, AnchorPath: null, AnchorLine: null,
            PlatformThreadNodeId: "PRRT_kwDOABCDEF");

        var withoutNode = withNode with { PlatformThreadNodeId = null, LastUpdatedAt = t2 };

        await threadRepo.UpsertManyAsync(row.Identity, new[] { withNode }, syncedAt: t1, CancellationToken.None);
        await threadRepo.UpsertManyAsync(row.Identity, new[] { withoutNode }, syncedAt: t2, CancellationToken.None);

        var open = await threadRepo.GetOpenThreadsAsync(row.Identity, CancellationToken.None);
        open[0].PlatformThreadNodeId.Should().Be("PRRT_kwDOABCDEF");
    }

    [Fact]
    public async Task ObservedThread_Reopen_Clears_ResolvedAt()
    {
        // Authoritative reopen semantics: if a thread was previously
        // resolved (either by sync or by local write-back) and the next
        // sync says IsResolved=false, resolved_at must be cleared.
        var prRepo = new PullRequestRepository(_db);
        var threadRepo = new ObservedThreadRepository(_db);
        var row = SampleRow();
        await prRepo.UpsertAsync(row, CancellationToken.None);

        var t1 = DateTimeOffset.Parse("2026-05-17T10:00:00Z");
        var t2 = DateTimeOffset.Parse("2026-05-17T11:00:00Z");

        var resolvedThread = new RemoteThread(
            PlatformThreadId: "review-comment:99",
            Kind: ThreadKind.ReviewComment,
            AuthorLogin: "jmprieur", IsBot: false, BotKind: null,
            IsResolved: true,
            CreatedAt: t1, LastUpdatedAt: t1, RawJson: "{}",
            PlatformThreadNodeId: "PRRT_reopen");

        var reopenedThread = resolvedThread with { IsResolved = false, LastUpdatedAt = t2 };

        await threadRepo.UpsertManyAsync(row.Identity, new[] { resolvedThread }, syncedAt: t1, CancellationToken.None);
        (await threadRepo.GetOpenThreadsAsync(row.Identity, CancellationToken.None)).Should().BeEmpty();

        await threadRepo.UpsertManyAsync(row.Identity, new[] { reopenedThread }, syncedAt: t2, CancellationToken.None);
        var open = await threadRepo.GetOpenThreadsAsync(row.Identity, CancellationToken.None);
        open.Should().ContainSingle();
        open[0].ResolvedAt.Should().BeNull();
    }

    [Fact]
    public async Task MarkResolvedByNodeIdsAsync_Sets_ResolvedAt_On_All_Rows_Sharing_Node()
    {
        // A single GraphQL thread can contain multiple REST comments
        // (root + replies). One mutation must resolve all of them locally.
        var prRepo = new PullRequestRepository(_db);
        var threadRepo = new ObservedThreadRepository(_db);
        var row = SampleRow();
        await prRepo.UpsertAsync(row, CancellationToken.None);

        var t = DateTimeOffset.Parse("2026-05-17T10:00:00Z");
        var commonNode = "PRRT_shared";
        var rootComment = new RemoteThread(
            PlatformThreadId: "review-comment:100",
            Kind: ThreadKind.ReviewComment,
            AuthorLogin: "Copilot", IsBot: true, BotKind: PrInbox.Core.Models.BotKind.CopilotReview,
            IsResolved: false,
            CreatedAt: t, LastUpdatedAt: t, RawJson: "{}",
            PlatformThreadNodeId: commonNode);
        var replyComment = rootComment with { PlatformThreadId = "review-comment:101" };
        var unrelatedComment = rootComment with
        {
            PlatformThreadId = "review-comment:200",
            PlatformThreadNodeId = "PRRT_other",
        };

        await threadRepo.UpsertManyAsync(
            row.Identity,
            new[] { rootComment, replyComment, unrelatedComment },
            syncedAt: t,
            CancellationToken.None);

        var resolvedAt = DateTimeOffset.Parse("2026-05-17T12:00:00Z");
        var affected = await threadRepo.MarkResolvedByNodeIdsAsync(
            row.Identity,
            new[] { commonNode },
            resolvedAt,
            CancellationToken.None);

        affected.Should().Be(2);
        var open = await threadRepo.GetOpenThreadsAsync(row.Identity, CancellationToken.None);
        open.Should().ContainSingle(); // only the unrelated row remains open
        open[0].PlatformThreadNodeId.Should().Be("PRRT_other");
    }

    [Fact]
    public async Task MarkResolvedByNodeIdsAsync_Is_Idempotent_For_Already_Resolved_Rows()
    {
        var prRepo = new PullRequestRepository(_db);
        var threadRepo = new ObservedThreadRepository(_db);
        var row = SampleRow();
        await prRepo.UpsertAsync(row, CancellationToken.None);

        var t = DateTimeOffset.Parse("2026-05-17T10:00:00Z");
        var thread = new RemoteThread(
            PlatformThreadId: "review-comment:42",
            Kind: ThreadKind.ReviewComment,
            AuthorLogin: "Copilot", IsBot: true, BotKind: PrInbox.Core.Models.BotKind.CopilotReview,
            IsResolved: true, // already resolved upstream
            CreatedAt: t, LastUpdatedAt: t, RawJson: "{}",
            PlatformThreadNodeId: "PRRT_already");
        await threadRepo.UpsertManyAsync(row.Identity, new[] { thread }, syncedAt: t, CancellationToken.None);

        // Second call should affect zero rows (already-resolved guard).
        var affected = await threadRepo.MarkResolvedByNodeIdsAsync(
            row.Identity,
            new[] { "PRRT_already" },
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        affected.Should().Be(0);
    }

    [Fact]
    public async Task PullRequest_Upsert_Persists_LastUpstreamUpdatedAt()
    {
        var repo = new PullRequestRepository(_db);
        var upstream = DateTimeOffset.Parse("2026-05-18T15:30:00Z");
        var row = SampleRow() with { LastUpstreamUpdatedAt = upstream };

        await repo.UpsertAsync(row, CancellationToken.None);
        var fetched = await repo.GetAsync(row.Identity.Url, CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.LastUpstreamUpdatedAt.Should().Be(upstream);
    }

    [Fact]
    public async Task PullRequest_Upsert_With_Null_LastUpstreamUpdatedAt_Persists_Null()
    {
        // Default is null; backfill window before the next fast-sync touches
        // pre-existing rows must round-trip through SQLite cleanly.
        var repo = new PullRequestRepository(_db);
        var row = SampleRow(); // LastUpstreamUpdatedAt defaults to null

        await repo.UpsertAsync(row, CancellationToken.None);
        var fetched = await repo.GetAsync(row.Identity.Url, CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.LastUpstreamUpdatedAt.Should().BeNull();
    }

    [Fact]
    public async Task PullRequest_Upsert_Overwrites_LastUpstreamUpdatedAt_On_Conflict()
    {
        // Each fast-sync brings a fresh upstream timestamp; ON CONFLICT must
        // overwrite with the new value, not coalesce to the old one.
        var repo = new PullRequestRepository(_db);
        var first = SampleRow() with
        {
            LastUpstreamUpdatedAt = DateTimeOffset.Parse("2026-05-18T10:00:00Z"),
        };
        var newer = DateTimeOffset.Parse("2026-05-18T16:45:00Z");
        var second = first with { LastUpstreamUpdatedAt = newer };

        await repo.UpsertAsync(first, CancellationToken.None);
        await repo.UpsertAsync(second, CancellationToken.None);
        var fetched = await repo.GetAsync(first.Identity.Url, CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.LastUpstreamUpdatedAt.Should().Be(newer);
    }

    [Fact]
    public async Task MarkDoneAsync_Persists_Sha_And_Timestamp()
    {
        var repo = new PullRequestRepository(_db);
        var row = SampleRow();
        await repo.UpsertAsync(row, CancellationToken.None);

        var when = DateTimeOffset.Parse("2026-05-20T19:00:00Z");
        await repo.MarkDoneAsync(row.Identity.Url, "abc123", when, CancellationToken.None);
        var fetched = await repo.GetAsync(row.Identity.Url, CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.MarkedDoneHeadSha.Should().Be("abc123");
        fetched.MarkedDoneAt.Should().Be(when);
    }

    [Fact]
    public async Task ClearDoneAsync_Removes_Sha_And_Timestamp()
    {
        var repo = new PullRequestRepository(_db);
        var row = SampleRow();
        await repo.UpsertAsync(row, CancellationToken.None);
        await repo.MarkDoneAsync(row.Identity.Url, "abc123", DateTimeOffset.UtcNow, CancellationToken.None);

        await repo.ClearDoneAsync(row.Identity.Url, CancellationToken.None);
        var fetched = await repo.GetAsync(row.Identity.Url, CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.MarkedDoneHeadSha.Should().BeNull();
        fetched.MarkedDoneAt.Should().BeNull();
    }

    [Fact]
    public async Task Upsert_Preserves_MarkedDone_Across_Subsequent_Sync_Writes()
    {
        // The fast-sync UPSERT path writes everything the sync layer
        // knows. It must NOT touch marked_done_* — only the dedicated
        // MarkDoneAsync / ClearDoneAsync entrypoints do.
        var repo = new PullRequestRepository(_db);
        var row = SampleRow();
        await repo.UpsertAsync(row, CancellationToken.None);

        var when = DateTimeOffset.Parse("2026-05-20T19:00:00Z");
        await repo.MarkDoneAsync(row.Identity.Url, "old-sha", when, CancellationToken.None);

        // Simulate the next fast-sync touch — same row, mutable fields
        // refreshed but no done-related data carried.
        var refreshed = row with
        {
            LastSyncedAt = row.LastSyncedAt.AddMinutes(5),
            Title = "Updated title",
        };
        await repo.UpsertAsync(refreshed, CancellationToken.None);

        var fetched = await repo.GetAsync(row.Identity.Url, CancellationToken.None);
        fetched.Should().NotBeNull();
        fetched!.MarkedDoneHeadSha.Should().Be("old-sha");
        fetched.MarkedDoneAt.Should().Be(when);
        fetched.Title.Should().Be("Updated title");
    }

    [Fact]
    public async Task FlagAsync_Persists_Timestamp_And_Unflag_Clears()
    {
        var repo = new PullRequestRepository(_db);
        var row = SampleRow();
        await repo.UpsertAsync(row, CancellationToken.None);

        var when = DateTimeOffset.Parse("2026-05-21T08:00:00Z");
        await repo.FlagAsync(row.Identity.Url, when, CancellationToken.None);
        var flagged = await repo.GetAsync(row.Identity.Url, CancellationToken.None);

        flagged.Should().NotBeNull();
        flagged!.FlaggedAt.Should().Be(when);

        await repo.UnflagAsync(row.Identity.Url, CancellationToken.None);
        var cleared = await repo.GetAsync(row.Identity.Url, CancellationToken.None);
        cleared!.FlaggedAt.Should().BeNull();
    }

    [Fact]
    public async Task Upsert_Preserves_FlaggedAt_Across_Subsequent_Sync_Writes()
    {
        // Mirror invariant for Flag: the sync UPSERT path must not touch
        // flagged_at — only FlagAsync / UnflagAsync do.
        var repo = new PullRequestRepository(_db);
        var row = SampleRow();
        await repo.UpsertAsync(row, CancellationToken.None);

        var when = DateTimeOffset.Parse("2026-05-21T08:00:00Z");
        await repo.FlagAsync(row.Identity.Url, when, CancellationToken.None);

        var refreshed = row with
        {
            LastSyncedAt = row.LastSyncedAt.AddMinutes(5),
            Title = "Updated title",
        };
        await repo.UpsertAsync(refreshed, CancellationToken.None);

        var fetched = await repo.GetAsync(row.Identity.Url, CancellationToken.None);
        fetched.Should().NotBeNull();
        fetched!.FlaggedAt.Should().Be(when);
        fetched.Title.Should().Be("Updated title");
    }
}

