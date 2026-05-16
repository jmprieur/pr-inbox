using PrInbox.Core.Models;
using PrInbox.Core.Storage;

namespace PrInbox.Tests.Storage;

/// <summary>
/// Tests for the post-v1 filter / sweep columns added in migration 005:
/// is_ignored, disappeared_at, last_swept_at, plus the
/// <see cref="UiPreferencesRepository"/> key/value store.
/// </summary>
public class PostV1FiltersTests : IAsyncLifetime
{
    private string _connString = string.Empty;
    private PrInboxDb _db = null!;
    private Microsoft.Data.Sqlite.SqliteConnection _keepAlive = null!;

    public async Task InitializeAsync()
    {
        _connString = PrInboxDb.InMemoryConnectionString($"postv1-{Guid.NewGuid():N}");
        _db = new PrInboxDb(_connString);
        _keepAlive = await _db.OpenAsync();
        await new MigrationRunner().MigrateAsync(_connString);
    }

    public async Task DisposeAsync()
    {
        await _keepAlive.DisposeAsync();
    }

    private static PullRequestRow BuildRow(
        string url,
        string sourceId = "gh.com:emu",
        string identity = "jmprieur_microsoft",
        PullRequestStatus status = PullRequestStatus.Open,
        DateTimeOffset? lastSynced = null)
    {
        var when = lastSynced ?? DateTimeOffset.Parse("2026-05-13T20:30:00Z");
        return new PullRequestRow(
            Identity: new PrIdentity(url, $"stable:{url.GetHashCode():X}"),
            SourceKind: SourceKind.GitHub,
            SourceId: sourceId,
            DisplayRepo: "1ES/sample",
            Number: 1,
            Title: "Sample",
            AuthorLogin: "octocat",
            Url: url,
            Status: status,
            TrackingReason: TrackingReason.Assigned,
            IdentityUsed: identity,
            FirstSeenAt: when,
            LastSyncedAt: when,
            EnrichState: EnrichState.Basic,
            LastBriefedHeadSha: null,
            LastReviewRunHeadSha: null,
            LastPostedReviewHeadSha: null);
    }

    [Fact]
    public async Task SetIgnored_Round_Trips_Through_Get()
    {
        var repo = new PullRequestRepository(_db);
        var row = BuildRow("https://github.com/o/r/pull/1");
        await repo.UpsertAsync(row, CancellationToken.None);

        (await repo.GetAsync(row.Url, CancellationToken.None))!.IsIgnored.Should().BeFalse();

        await repo.SetIgnoredAsync(row.Url, true, CancellationToken.None);
        (await repo.GetAsync(row.Url, CancellationToken.None))!.IsIgnored.Should().BeTrue();

        await repo.SetIgnoredAsync(row.Url, false, CancellationToken.None);
        (await repo.GetAsync(row.Url, CancellationToken.None))!.IsIgnored.Should().BeFalse();
    }

    [Fact]
    public async Task SetIgnored_Survives_Subsequent_Upsert()
    {
        // Sync runs upsert again every cycle; the ignore flag must NOT be
        // reset when the row gets re-upserted with default values.
        var repo = new PullRequestRepository(_db);
        var row = BuildRow("https://github.com/o/r/pull/2");
        await repo.UpsertAsync(row, CancellationToken.None);
        await repo.SetIgnoredAsync(row.Url, true, CancellationToken.None);

        // Simulate the next fast-sync touching the same row.
        await repo.UpsertAsync(row with { Title = "renamed" }, CancellationToken.None);

        var fetched = await repo.GetAsync(row.Url, CancellationToken.None);
        fetched!.IsIgnored.Should().BeTrue();
        fetched.Title.Should().Be("renamed");
    }

    [Fact]
    public async Task MarkSwept_And_SetDisappeared_RoundTrip()
    {
        var repo = new PullRequestRepository(_db);
        var row = BuildRow("https://github.com/o/r/pull/3");
        await repo.UpsertAsync(row, CancellationToken.None);

        var swept = DateTimeOffset.Parse("2026-05-15T12:00:00Z");
        await repo.MarkSweptAsync(row.Url, swept, CancellationToken.None);

        var disappeared = DateTimeOffset.Parse("2026-05-15T13:00:00Z");
        await repo.SetDisappearedAtAsync(row.Url, disappeared, CancellationToken.None);

        var fetched = await repo.GetAsync(row.Url, CancellationToken.None);
        fetched!.LastSweptAt!.Value.ToUniversalTime().Should().Be(swept);
        fetched.DisappearedAt!.Value.ToUniversalTime().Should().Be(disappeared);

        // Clearing disappeared_at sets it back to null.
        await repo.SetDisappearedAtAsync(row.Url, null, CancellationToken.None);
        (await repo.GetAsync(row.Url, CancellationToken.None))!.DisappearedAt.Should().BeNull();
    }

    [Fact]
    public async Task UpdateStatus_Persists_New_Status()
    {
        var repo = new PullRequestRepository(_db);
        var row = BuildRow("https://github.com/o/r/pull/4");
        await repo.UpsertAsync(row, CancellationToken.None);

        await repo.UpdateStatusAsync(row.Url, PullRequestStatus.Merged, CancellationToken.None);

        (await repo.GetAsync(row.Url, CancellationToken.None))!.Status
            .Should().Be(PullRequestStatus.Merged);
    }

    [Fact]
    public async Task ListOpenUrlsByIdentity_Scopes_To_Binding()
    {
        var repo = new PullRequestRepository(_db);
        await repo.UpsertAsync(BuildRow("https://github.com/o/r/pull/10", sourceId: "gh.com:emu",    identity: "ident-a"), CancellationToken.None);
        await repo.UpsertAsync(BuildRow("https://github.com/o/r/pull/11", sourceId: "gh.com:emu",    identity: "ident-a"), CancellationToken.None);
        await repo.UpsertAsync(BuildRow("https://github.com/o/r/pull/12", sourceId: "gh.com:public", identity: "ident-b"), CancellationToken.None);
        await repo.UpsertAsync(BuildRow("https://github.com/o/r/pull/13", sourceId: "gh.com:emu",    identity: "ident-a", status: PullRequestStatus.Closed), CancellationToken.None);

        var urls = await repo.ListOpenUrlsByIdentityAsync("gh.com:emu", "ident-a", CancellationToken.None);
        urls.Should().BeEquivalentTo(new[]
        {
            "https://github.com/o/r/pull/10",
            "https://github.com/o/r/pull/11",
        });
    }

    [Fact]
    public async Task ListOldestSweptOpen_Returns_Never_Swept_First_Then_Oldest_Swept()
    {
        var repo = new PullRequestRepository(_db);

        // Seed three rows. Stagger their last_synced_at so the
        // never-swept tiebreaker is deterministic.
        var older = DateTimeOffset.Parse("2026-05-13T20:00:00Z");
        var middle = DateTimeOffset.Parse("2026-05-13T21:00:00Z");
        var newer = DateTimeOffset.Parse("2026-05-13T22:00:00Z");

        await repo.UpsertAsync(BuildRow("https://github.com/o/r/pull/20", lastSynced: older),  CancellationToken.None);
        await repo.UpsertAsync(BuildRow("https://github.com/o/r/pull/21", lastSynced: middle), CancellationToken.None);
        await repo.UpsertAsync(BuildRow("https://github.com/o/r/pull/22", lastSynced: newer),  CancellationToken.None);

        // 22 was already swept recently -> ranks last.
        await repo.MarkSweptAsync("https://github.com/o/r/pull/22",
            DateTimeOffset.Parse("2026-05-15T12:00:00Z"), CancellationToken.None);

        var pick = await repo.ListOldestSweptOpenAsync("gh.com:emu", "jmprieur_microsoft", limit: 2, CancellationToken.None);
        pick.Select(p => p.Url).Should().Equal(new[]
        {
            // Both never swept; older last_synced_at first.
            "https://github.com/o/r/pull/20",
            "https://github.com/o/r/pull/21",
        });

        // Ignored rows are excluded.
        await repo.SetIgnoredAsync("https://github.com/o/r/pull/20", true, CancellationToken.None);
        var pickAfterIgnore = await repo.ListOldestSweptOpenAsync("gh.com:emu", "jmprieur_microsoft", limit: 2, CancellationToken.None);
        pickAfterIgnore.Select(p => p.Url).Should().NotContain("https://github.com/o/r/pull/20");
    }

    [Fact]
    public async Task UiPreferences_Round_Trip_Strings_And_Bools()
    {
        var prefs = new UiPreferencesRepository(_db);

        (await prefs.GetAsync("missing", defaultValue: "fallback")).Should().Be("fallback");
        (await prefs.GetBoolAsync("missing.flag", defaultValue: true)).Should().BeTrue();

        await prefs.SetAsync("inbox.source_filter", "[\"EMU\",\"public\"]");
        (await prefs.GetAsync("inbox.source_filter")).Should().Be("[\"EMU\",\"public\"]");

        await prefs.SetBoolAsync("inbox.show_closed", true);
        (await prefs.GetBoolAsync("inbox.show_closed", defaultValue: false)).Should().BeTrue();

        // Update overwrites.
        await prefs.SetBoolAsync("inbox.show_closed", false);
        (await prefs.GetBoolAsync("inbox.show_closed", defaultValue: true)).Should().BeFalse();

        // Null clears.
        await prefs.SetAsync("inbox.source_filter", null);
        (await prefs.GetAsync("inbox.source_filter")).Should().BeNull();
    }
}
