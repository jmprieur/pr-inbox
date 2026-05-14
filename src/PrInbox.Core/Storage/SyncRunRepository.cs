using Microsoft.Data.Sqlite;
using PrInbox.Core.Models;

namespace PrInbox.Core.Storage;

/// <summary>
/// Repository for <c>sync_runs</c>. Tracks each (source, identity) sync attempt
/// from start to finish; <c>list</c> reads the latest row per source to surface
/// partial-failure staleness.
/// </summary>
public sealed class SyncRunRepository
{
    private readonly PrInboxDb _db;

    public SyncRunRepository(PrInboxDb db) => _db = db;

    /// <summary>
    /// Inserts a row in state <c>running</c>. Returns the row id.
    /// </summary>
    public async Task<long> StartAsync(string sourceId, string identityUsed, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sync_runs (source_id, identity_used, started_at, status)
            VALUES ($sourceId, $identityUsed, $startedAt, 'running')
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$sourceId", sourceId);
        cmd.Parameters.AddWithValue("$identityUsed", identityUsed);
        cmd.Parameters.AddWithValue("$startedAt",
            PullRequestRepository.FormatTimestamp(DateTimeOffset.UtcNow));

        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result);
    }

    /// <summary>
    /// Finalizes a sync run with status + prs_seen + optional error.
    /// </summary>
    public async Task CompleteAsync(long id, SyncRunStatus status, int prsSeen, string? error, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE sync_runs
            SET completed_at = $completedAt,
                status       = $status,
                prs_seen     = $prsSeen,
                error        = $error
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$completedAt",
            PullRequestRepository.FormatTimestamp(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$status", SyncRunStatusToDb(status));
        cmd.Parameters.AddWithValue("$prsSeen", prsSeen);
        cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Returns the most recent sync_runs row for each (source, identity) pair.
    /// Used by <c>list</c> to render the source-staleness footer.
    /// </summary>
    public async Task<IReadOnlyList<SyncRunRow>> GetLatestPerSourceAsync(CancellationToken ct)
    {
        var rows = new List<SyncRunRow>();
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.* FROM sync_runs s
            INNER JOIN (
              SELECT source_id, identity_used, MAX(started_at) AS max_started
              FROM sync_runs
              GROUP BY source_id, identity_used
            ) latest
              ON s.source_id = latest.source_id
             AND s.identity_used = latest.identity_used
             AND s.started_at = latest.max_started;
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(MapRow(reader));
        }
        return rows;
    }

    private static SyncRunRow MapRow(SqliteDataReader reader)
    {
        return new SyncRunRow(
            Id: reader.GetInt64(reader.GetOrdinal("id")),
            SourceId: reader.GetString(reader.GetOrdinal("source_id")),
            IdentityUsed: reader.GetString(reader.GetOrdinal("identity_used")),
            StartedAt: DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("started_at"))),
            CompletedAt: reader.IsDBNull(reader.GetOrdinal("completed_at"))
                ? null
                : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("completed_at"))),
            Status: ParseStatus(reader.GetString(reader.GetOrdinal("status"))),
            Error: reader.IsDBNull(reader.GetOrdinal("error"))
                ? null
                : reader.GetString(reader.GetOrdinal("error")),
            PrsSeen: reader.GetInt32(reader.GetOrdinal("prs_seen")));
    }

    private static SyncRunStatus ParseStatus(string value) => value switch
    {
        "running" => SyncRunStatus.Running,
        "ok" => SyncRunStatus.Ok,
        "partial" => SyncRunStatus.Partial,
        "failed" => SyncRunStatus.Failed,
        "rate_limited" => SyncRunStatus.RateLimited,
        _ => throw new InvalidOperationException($"Unknown sync_runs status '{value}'."),
    };

    private static string SyncRunStatusToDb(SyncRunStatus status) => status switch
    {
        SyncRunStatus.Running => "running",
        SyncRunStatus.Ok => "ok",
        SyncRunStatus.Partial => "partial",
        SyncRunStatus.Failed => "failed",
        SyncRunStatus.RateLimited => "rate_limited",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}
