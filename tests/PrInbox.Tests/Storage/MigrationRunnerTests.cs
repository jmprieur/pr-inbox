using PrInbox.Core.Storage;

namespace PrInbox.Tests.Storage;

/// <summary>
/// Migration runner tests: idempotency, schema_version tracking, and
/// the fact that the initial migration produces a usable schema.
/// </summary>
public class MigrationRunnerTests
{
    [Fact]
    public void LoadEmbeddedMigrations_Returns_The_Initial_Migration()
    {
        var migrations = MigrationRunner.LoadEmbeddedMigrations();
        migrations.Should().NotBeEmpty();
        migrations[0].Version.Should().Be(1);
        migrations[0].Name.Should().Be("initial");
        migrations[0].Sql.Should().Contain("CREATE TABLE pull_requests");
    }

    [Fact]
    public async Task Migrate_On_Fresh_Db_Applies_Initial_And_Records_Version()
    {
        var conn = PrInboxDb.InMemoryConnectionString($"mig-{Guid.NewGuid():N}");
        // Hold a connection open so the shared in-memory cache persists.
        var db = new PrInboxDb(conn);
        await using var keepAlive = await db.OpenAsync();

        var runner = new MigrationRunner();
        var applied = await runner.MigrateAsync(conn);

        applied.Should().Be(1);

        await using var query = keepAlive.CreateCommand();
        query.CommandText = "SELECT version, name FROM schema_version;";
        await using var reader = await query.ExecuteReaderAsync();
        var rows = new List<(int version, string name)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetInt32(0), reader.GetString(1)));
        }
        rows.Should().ContainSingle(r => r.version == 1 && r.name == "initial");
    }

    [Fact]
    public async Task Migrate_Is_Idempotent_When_Already_Applied()
    {
        var conn = PrInboxDb.InMemoryConnectionString($"mig-{Guid.NewGuid():N}");
        var db = new PrInboxDb(conn);
        await using var keepAlive = await db.OpenAsync();

        var runner = new MigrationRunner();
        var firstApplied = await runner.MigrateAsync(conn);
        var secondApplied = await runner.MigrateAsync(conn);

        firstApplied.Should().Be(1);
        secondApplied.Should().Be(0);
    }

    [Fact]
    public async Task Migrate_Creates_All_Expected_Tables()
    {
        var conn = PrInboxDb.InMemoryConnectionString($"mig-{Guid.NewGuid():N}");
        var db = new PrInboxDb(conn);
        await using var keepAlive = await db.OpenAsync();

        var runner = new MigrationRunner();
        await runner.MigrateAsync(conn);

        var expected = new[]
        {
            "schema_version",
            "pull_requests",
            "pr_snapshots",
            "observed_threads",
            "review_runs",
            "posted_reviews",
            "sync_runs",
        };

        await using var cmd = keepAlive.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        var actual = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            actual.Add(reader.GetString(0));
        }

        actual.Should().Contain(expected);
    }
}
