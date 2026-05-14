using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PrInbox.Core.Storage;

/// <summary>
/// Owns the SQLite connection string and creates connections on demand.
/// Each repository takes a <see cref="PrInboxDb"/> and opens its own
/// short-lived connection per operation (SQLite is single-writer; many
/// short connections perform better than one shared connection).
/// </summary>
public sealed class PrInboxDb
{
    private readonly ILogger<PrInboxDb> _logger;

    public PrInboxDb(string connectionString, ILogger<PrInboxDb>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ConnectionString = connectionString;
        _logger = logger ?? NullLogger<PrInboxDb>.Instance;
    }

    public string ConnectionString { get; }

    /// <summary>
    /// Builds a SQLite connection string for the per-user DB at
    /// <c>%APPDATA%\PrInbox\pr-inbox.db</c>, creating the parent
    /// directory if missing.
    /// </summary>
    public static string DefaultUserConnectionString()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "PrInbox");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "pr-inbox.db");
        return $"Data Source={dbPath};";
    }

    /// <summary>
    /// Builds a connection string for an in-memory shared SQLite database.
    /// Useful for tests; the cache is shared per name so multiple
    /// connections see the same data.
    /// </summary>
    public static string InMemoryConnectionString(string cacheName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheName);
        return $"Data Source={cacheName};Mode=Memory;Cache=Shared";
    }

    /// <summary>
    /// Opens a new connection with <c>foreign_keys=ON</c>.
    /// Caller disposes.
    /// </summary>
    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA foreign_keys = ON;";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return conn;
    }
}
