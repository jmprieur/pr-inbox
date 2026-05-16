using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PrInbox.Core.Storage;

/// <summary>
/// Applies embedded SQL migrations to a SQLite database, in monotonic version
/// order, recording each application in <c>schema_version</c>.
/// </summary>
/// <remarks>
/// Migrations are embedded SQL files in <c>Storage/Migrations/NNN_name.sql</c>,
/// where <c>NNN</c> is a three-digit zero-padded version. The runner is
/// idempotent: applying a migration whose version is already in
/// <c>schema_version</c> is a no-op.
/// </remarks>
public sealed class MigrationRunner
{
    private readonly ILogger<MigrationRunner> _logger;

    public MigrationRunner(ILogger<MigrationRunner>? logger = null)
    {
        _logger = logger ?? NullLogger<MigrationRunner>.Instance;
    }

    /// <summary>
    /// Applies any pending migrations to the database represented by
    /// <paramref name="connectionString"/>. A backup of the database file
    /// is created next to it before a non-empty migration set is applied.
    /// </summary>
    /// <returns>The number of migrations applied (zero if up-to-date).</returns>
    public async Task<int> MigrateAsync(string connectionString, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var migrations = LoadEmbeddedMigrations();
        if (migrations.Count == 0)
        {
            _logger.LogWarning("No embedded migrations found.");
            return 0;
        }

        var dbPath = ExtractDataSource(connectionString);
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await EnsureSchemaVersionTableAsync(conn, ct);

        var applied = await GetAppliedVersionsAsync(conn, ct);
        var pending = migrations.Where(m => !applied.Contains(m.Version)).OrderBy(m => m.Version).ToList();
        if (pending.Count == 0)
        {
            _logger.LogDebug("Database up to date at version {Version}.",
                applied.Count == 0 ? 0 : applied.Max());
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(dbPath) && File.Exists(dbPath))
        {
            var backupPath = $"{dbPath}.backup-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}";
            File.Copy(dbPath, backupPath, overwrite: false);
            _logger.LogInformation("Backed up DB to {BackupPath} before applying {Count} migration(s).",
                backupPath, pending.Count);
        }

        // Disable foreign keys for the duration of migration application so
        // multi-step backfills (e.g. updating a parent PK after its children)
        // can run in any order without tripping referential integrity.
        // Re-enabled in the finally block. Microsoft.Data.Sqlite enables FKs
        // by default on Open, so this is normally a no-op without our flip.
        await SetForeignKeysAsync(conn, enabled: false, ct);
        var count = 0;
        try
        {
            foreach (var migration in pending)
            {
                ct.ThrowIfCancellationRequested();
                await ApplyMigrationAsync(conn, migration, ct);
                count++;
            }
        }
        finally
        {
            await SetForeignKeysAsync(conn, enabled: true, ct);
        }

        _logger.LogInformation("Applied {Count} migration(s). Latest version: {Version}.",
            count, pending[^1].Version);
        return count;
    }

    private static async Task SetForeignKeysAsync(SqliteConnection conn, bool enabled, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = enabled ? "PRAGMA foreign_keys = ON;" : "PRAGMA foreign_keys = OFF;";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task EnsureSchemaVersionTableAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_version (
              version    INTEGER PRIMARY KEY,
              name       TEXT    NOT NULL,
              applied_at TEXT    NOT NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<HashSet<int>> GetAppliedVersionsAsync(SqliteConnection conn, CancellationToken ct)
    {
        var set = new HashSet<int>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version FROM schema_version;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            set.Add(reader.GetInt32(0));
        }
        return set;
    }

    private async Task ApplyMigrationAsync(SqliteConnection conn, EmbeddedMigration migration, CancellationToken ct)
    {
        _logger.LogInformation("Applying migration {Version} ({Name})...", migration.Version, migration.Name);
        await using var transaction = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        try
        {
            await using (var migrationCmd = conn.CreateCommand())
            {
                migrationCmd.Transaction = transaction;

                // The 001_initial.sql script includes a top-level PRAGMA that
                // would be silently ignored inside a transaction; we already
                // do the equivalent at the connection level. Strip leading
                // PRAGMA lines so the rest of the script runs cleanly.
                migrationCmd.CommandText = StripLeadingPragmas(migration.Sql);
                await migrationCmd.ExecuteNonQueryAsync(ct);
            }

            await using (var recordCmd = conn.CreateCommand())
            {
                recordCmd.Transaction = transaction;
                recordCmd.CommandText = """
                    INSERT INTO schema_version (version, name, applied_at)
                    VALUES ($version, $name, $appliedAt);
                    """;
                recordCmd.Parameters.AddWithValue("$version", migration.Version);
                recordCmd.Parameters.AddWithValue("$name", migration.Name);
                recordCmd.Parameters.AddWithValue("$appliedAt",
                    DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                await recordCmd.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    private static string StripLeadingPragmas(string sql)
    {
        var lines = sql.Split('\n');
        var skip = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("--", StringComparison.Ordinal) ||
                trimmed.Length == 0 ||
                trimmed.StartsWith("PRAGMA ", StringComparison.OrdinalIgnoreCase))
            {
                skip = i + 1;
                continue;
            }
            break;
        }
        return string.Join('\n', lines.Skip(skip));
    }

    /// <summary>
    /// Loads embedded migrations from <c>PrInbox.Core.dll</c> resources.
    /// Resource names look like <c>PrInbox.Core.Storage.Migrations.001_initial.sql</c>.
    /// </summary>
    internal static IReadOnlyList<EmbeddedMigration> LoadEmbeddedMigrations()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        var prefix = $"{typeof(MigrationRunner).Namespace}.Migrations.";
        var migrations = new List<EmbeddedMigration>();

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(prefix, StringComparison.Ordinal) ||
                !resourceName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fileName = resourceName.Substring(prefix.Length); // 001_initial.sql
            var match = System.Text.RegularExpressions.Regex.Match(fileName, @"^(\d+)_([^.]+)\.sql$");
            if (!match.Success)
            {
                throw new InvalidOperationException(
                    $"Embedded migration '{resourceName}' does not match the expected 'NNN_name.sql' pattern.");
            }

            var version = int.Parse(match.Groups[1].Value);
            var name = match.Groups[2].Value;

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Failed to open embedded resource '{resourceName}'.");
            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();

            migrations.Add(new EmbeddedMigration(version, name, sql));
        }

        return migrations.OrderBy(m => m.Version).ToList();
    }

    private static string ExtractDataSource(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        return builder.DataSource ?? string.Empty;
    }

    internal sealed record EmbeddedMigration(int Version, string Name, string Sql);
}
