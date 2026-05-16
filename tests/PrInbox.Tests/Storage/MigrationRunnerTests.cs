using PrInbox.Core.Storage;

namespace PrInbox.Tests.Storage;

/// <summary>
/// Migration runner tests: idempotency, schema_version tracking, and
/// the fact that the initial migration produces a usable schema.
/// </summary>
public class MigrationRunnerTests
{
    [Fact]
    public void LoadEmbeddedMigrations_Returns_All_Known_Migrations()
    {
        var migrations = MigrationRunner.LoadEmbeddedMigrations();
        migrations.Should().NotBeEmpty();
        migrations[0].Version.Should().Be(1);
        migrations[0].Name.Should().Be("initial");
        migrations[0].Sql.Should().Contain("CREATE TABLE pull_requests");

        migrations.Should().Contain(m => m.Version == 2 && m.Name == "url_as_identity");
    }

    [Fact]
    public async Task Migrate_On_Fresh_Db_Applies_All_And_Records_Versions()
    {
        var conn = PrInboxDb.InMemoryConnectionString($"mig-{Guid.NewGuid():N}");
        // Hold a connection open so the shared in-memory cache persists.
        var db = new PrInboxDb(conn);
        await using var keepAlive = await db.OpenAsync();

        var runner = new MigrationRunner();
        var applied = await runner.MigrateAsync(conn);

        applied.Should().Be(MigrationRunner.LoadEmbeddedMigrations().Count);

        await using var query = keepAlive.CreateCommand();
        query.CommandText = "SELECT version, name FROM schema_version ORDER BY version;";
        await using var reader = await query.ExecuteReaderAsync();
        var rows = new List<(int version, string name)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetInt32(0), reader.GetString(1)));
        }
        rows.Should().Contain(r => r.version == 1 && r.name == "initial");
        rows.Should().Contain(r => r.version == 2 && r.name == "url_as_identity");
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

        firstApplied.Should().Be(MigrationRunner.LoadEmbeddedMigrations().Count);
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
            "pr_source_bindings",
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

    [Fact]
    public async Task Migration_002_Aligns_PrIdentity_With_Url_On_Legacy_Rows()
    {
        // Simulate a v1 DB by applying only migration 001, seeding rows with
        // the legacy pr_identity = "gh.com:owner/repo#N", then applying 002
        // and asserting the backfill realigns pr_identity to the url column.
        var conn = PrInboxDb.InMemoryConnectionString($"mig-{Guid.NewGuid():N}");
        var db = new PrInboxDb(conn);
        await using var keepAlive = await db.OpenAsync();

        // Manually apply only migration 001 (we cannot ask the runner to stop
        // at a given version, so we lift the SQL directly).
        var all = MigrationRunner.LoadEmbeddedMigrations();
        var m001 = all.Single(m => m.Version == 1);
        await ExecuteMigrationDirectlyAsync(keepAlive, m001);

        // Seed pre-v2 shape: pr_identity = legacy display string, url = canonical URL.
        await ExecuteAsync(keepAlive, """
            INSERT INTO pull_requests (
              pr_identity, stable_identity, source_id, source_kind, display_repo, number,
              title, author_login, url, status, tracking_reason, identity_used,
              first_seen_at, last_synced_at
            ) VALUES (
              'gh.com:agency-microsoft/playground#4248',
              'gh.com:100#4248',
              'gh.com', 'github', 'agency-microsoft/playground', 4248,
              'Sample PR', 'octocat',
              'https://github.com/agency-microsoft/playground/pull/4248',
              'open', 'assigned', 'jmprieur_public',
              '2026-05-13T20:00:00Z', '2026-05-13T20:30:00Z'
            );
            """);
        await ExecuteAsync(keepAlive, """
            INSERT INTO pr_snapshots (
              pr_identity, synced_at, head_sha, base_sha, ordered_commit_shas, pr_state
            ) VALUES (
              'gh.com:agency-microsoft/playground#4248',
              '2026-05-13T20:30:00Z', 'abc', 'base', '["abc"]', 'open'
            );
            """);
        await ExecuteAsync(keepAlive, """
            INSERT INTO observed_threads (
              pr_identity, platform_thread_id, kind, first_seen_at, last_seen_at
            ) VALUES (
              'gh.com:agency-microsoft/playground#4248', 't1', 'review_comment',
              '2026-05-13T20:30:00Z', '2026-05-13T20:30:00Z'
            );
            """);

        // Apply 002 (and any later migrations) via the runner. It should
        // record only the remaining versions; 001 is already in schema_version.
        // (We must create the schema_version table first because the runner
        // expects to find it; we then pre-seed it with version 1 to mark the
        // initial migration as already applied.)
        await ExecuteAsync(keepAlive, """
            CREATE TABLE schema_version (
              version    INTEGER PRIMARY KEY,
              name       TEXT    NOT NULL,
              applied_at TEXT    NOT NULL
            );
            """);
        await ExecuteAsync(keepAlive, "INSERT INTO schema_version (version, name, applied_at) VALUES (1, 'initial', '2026-05-13T20:00:00Z');");
        var runner = new MigrationRunner();
        var applied = await runner.MigrateAsync(conn);
        applied.Should().Be(all.Count - 1);

        // Parent pr_identity is now the URL.
        var parentId = await ScalarStringAsync(keepAlive,
            "SELECT pr_identity FROM pull_requests WHERE stable_identity = 'gh.com:100#4248';");
        parentId.Should().Be("https://github.com/agency-microsoft/playground/pull/4248");

        // Children point at the URL too.
        var childId = await ScalarStringAsync(keepAlive,
            "SELECT pr_identity FROM pr_snapshots LIMIT 1;");
        childId.Should().Be("https://github.com/agency-microsoft/playground/pull/4248");

        var threadId = await ScalarStringAsync(keepAlive,
            "SELECT pr_identity FROM observed_threads LIMIT 1;");
        threadId.Should().Be("https://github.com/agency-microsoft/playground/pull/4248");

        // Junction is seeded with one binding row per PR.
        var bindingCount = await ScalarLongAsync(keepAlive,
            "SELECT COUNT(*) FROM pr_source_bindings;");
        bindingCount.Should().Be(1L);

        var (bUrl, bSource, bIdentity) = await BindingRowAsync(keepAlive);
        bUrl.Should().Be("https://github.com/agency-microsoft/playground/pull/4248");
        bSource.Should().Be("gh.com");
        bIdentity.Should().Be("jmprieur_public");
    }

    private static async Task ExecuteMigrationDirectlyAsync(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        MigrationRunner.EmbeddedMigration migration)
    {
        // Mirror MigrationRunner's PRAGMA strip + transaction execution
        // without recording in schema_version (the test wants to control that).
        var lines = migration.Sql.Split('\n');
        var skip = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].TrimStart();
            if (t.StartsWith("--", StringComparison.Ordinal) || t.Length == 0 ||
                t.StartsWith("PRAGMA ", StringComparison.OrdinalIgnoreCase))
            {
                skip = i + 1;
                continue;
            }
            break;
        }
        var sql = string.Join('\n', lines.Skip(skip));
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(Microsoft.Data.Sqlite.SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string> ScalarStringAsync(Microsoft.Data.Sqlite.SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync();
        return result?.ToString() ?? string.Empty;
    }

    private static async Task<long> ScalarLongAsync(Microsoft.Data.Sqlite.SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }

    private static async Task<(string url, string source, string identity)> BindingRowAsync(
        Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pr_identity, source_id, identity_used FROM pr_source_bindings LIMIT 1;";
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }
}
