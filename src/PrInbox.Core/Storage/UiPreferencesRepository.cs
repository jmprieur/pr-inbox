using Microsoft.Data.Sqlite;

namespace PrInbox.Core.Storage;

/// <summary>
/// Single-key, single-value store for inbox-side UI preferences (toggle
/// states, filter chip selections). Backed by the <c>ui_preferences</c>
/// table introduced in migration 005.
/// </summary>
/// <remarks>
/// Values are stored as TEXT. Callers that want to store non-string data
/// (booleans, lists) should serialize to JSON before <see cref="SetAsync"/>
/// and parse on <see cref="GetAsync"/>.
/// </remarks>
public sealed class UiPreferencesRepository
{
    private readonly PrInboxDb _db;

    public UiPreferencesRepository(PrInboxDb db)
    {
        _db = db;
    }

    /// <summary>
    /// Returns the stored string for <paramref name="key"/>, or
    /// <paramref name="defaultValue"/> if missing.
    /// </summary>
    public async Task<string?> GetAsync(string key, string? defaultValue = null, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM ui_preferences WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is string s ? s : defaultValue;
    }

    /// <summary>
    /// Upserts the value for <paramref name="key"/>. A null
    /// <paramref name="value"/> deletes the row (equivalent to "use the
    /// default").
    /// </summary>
    public async Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        if (value is null)
        {
            await using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM ui_preferences WHERE key = $key;";
            del.Parameters.AddWithValue("$key", key);
            await del.ExecuteNonQueryAsync(ct);
            return;
        }
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ui_preferences (key, value, updated_at)
            VALUES ($key, $value, $now)
            ON CONFLICT(key) DO UPDATE SET
              value      = excluded.value,
              updated_at = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.Parameters.AddWithValue("$now",
            DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Convenience: read a bool flag (defaults to <paramref name="defaultValue"/>).
    /// </summary>
    public async Task<bool> GetBoolAsync(string key, bool defaultValue = false, CancellationToken ct = default)
    {
        var raw = await GetAsync(key, null, ct);
        return raw is null ? defaultValue : bool.TryParse(raw, out var b) ? b : defaultValue;
    }

    /// <summary>
    /// Convenience: write a bool flag.
    /// </summary>
    public Task SetBoolAsync(string key, bool value, CancellationToken ct = default) =>
        SetAsync(key, value ? "true" : "false", ct);
}
