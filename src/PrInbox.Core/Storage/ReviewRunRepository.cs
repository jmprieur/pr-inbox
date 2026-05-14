using Microsoft.Data.Sqlite;
using PrInbox.Core.Models;

namespace PrInbox.Core.Storage;

/// <summary>
/// Repository for <c>review_runs</c>. Immutable inserts; no updates.
/// </summary>
public sealed class ReviewRunRepository
{
    private readonly PrInboxDb _db;

    public ReviewRunRepository(PrInboxDb db) => _db = db;

    /// <summary>
    /// Insert a new review run row. Returns the row id.
    /// </summary>
    public async Task<long> InsertAsync(
        PrIdentity identity,
        DateTimeOffset createdAt,
        string briefPath,
        string runDirectory,
        string headSha,
        string baseSha,
        ReviewRunStatus status,
        string? notes,
        CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO review_runs (
              pr_identity, created_at, brief_path, run_directory,
              head_sha, base_sha, status, notes
            ) VALUES (
              $prId, $createdAt, $briefPath, $runDir,
              $headSha, $baseSha, $status, $notes
            )
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$prId", identity.Display);
        cmd.Parameters.AddWithValue("$createdAt", PullRequestRepository.FormatTimestamp(createdAt));
        cmd.Parameters.AddWithValue("$briefPath", briefPath);
        cmd.Parameters.AddWithValue("$runDir", runDirectory);
        cmd.Parameters.AddWithValue("$headSha", headSha);
        cmd.Parameters.AddWithValue("$baseSha", baseSha);
        cmd.Parameters.AddWithValue("$status", ReviewRunStatusToDb(status));
        cmd.Parameters.AddWithValue("$notes", (object?)notes ?? DBNull.Value);

        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result);
    }

    /// <summary>
    /// Returns all review runs for a PR, newest-first.
    /// </summary>
    public async Task<IReadOnlyList<ReviewRunRow>> ListForPrAsync(PrIdentity identity, CancellationToken ct)
    {
        var rows = new List<ReviewRunRow>();
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM review_runs
            WHERE pr_identity = $id
            ORDER BY created_at DESC, id DESC;
            """;
        cmd.Parameters.AddWithValue("$id", identity.Display);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(MapRow(reader, identity));
        }
        return rows;
    }

    private static ReviewRunRow MapRow(SqliteDataReader reader, PrIdentity identity)
    {
        return new ReviewRunRow(
            Id: reader.GetInt64(reader.GetOrdinal("id")),
            Identity: identity,
            CreatedAt: DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
            BriefPath: reader.GetString(reader.GetOrdinal("brief_path")),
            RunDirectory: reader.GetString(reader.GetOrdinal("run_directory")),
            HeadSha: reader.GetString(reader.GetOrdinal("head_sha")),
            BaseSha: reader.GetString(reader.GetOrdinal("base_sha")),
            Status: ParseStatus(reader.GetString(reader.GetOrdinal("status"))),
            CopilotSessionId: reader.IsDBNull(reader.GetOrdinal("copilot_session_id"))
                ? null
                : reader.GetString(reader.GetOrdinal("copilot_session_id")),
            Notes: reader.IsDBNull(reader.GetOrdinal("notes"))
                ? null
                : reader.GetString(reader.GetOrdinal("notes")));
    }

    private static ReviewRunStatus ParseStatus(string value) => value switch
    {
        "generated" => ReviewRunStatus.Generated,
        "session_started" => ReviewRunStatus.SessionStarted,
        "abandoned" => ReviewRunStatus.Abandoned,
        "superseded" => ReviewRunStatus.Superseded,
        _ => throw new InvalidOperationException($"Unknown review_runs status '{value}'."),
    };

    private static string ReviewRunStatusToDb(ReviewRunStatus status) => status switch
    {
        ReviewRunStatus.Generated => "generated",
        ReviewRunStatus.SessionStarted => "session_started",
        ReviewRunStatus.Abandoned => "abandoned",
        ReviewRunStatus.Superseded => "superseded",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}
