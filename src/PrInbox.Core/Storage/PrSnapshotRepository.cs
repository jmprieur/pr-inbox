using System.Text.Json;
using Microsoft.Data.Sqlite;
using PrInbox.Core.Models;

namespace PrInbox.Core.Storage;

/// <summary>
/// Repository for <c>pr_snapshots</c>. Append-only; never updates rows.
/// </summary>
public sealed class PrSnapshotRepository
{
    private readonly PrInboxDb _db;

    public PrSnapshotRepository(PrInboxDb db) => _db = db;

    /// <summary>
    /// Inserts a snapshot only if the most recent snapshot for this PR differs
    /// in any tracked field. Otherwise this is a no-op (the caller still bumps
    /// <c>last_synced_at</c> on <c>pull_requests</c>).
    /// </summary>
    /// <remarks>
    /// New post-v0.2 dossier fields (<paramref name="mergeableState"/>,
    /// <paramref name="ciStatus"/>, <paramref name="files"/>) are persisted but
    /// intentionally <i>not</i> included in the dedup comparison: CI status
    /// flips frequently (rerun on push, scheduled re-evaluation) and would
    /// otherwise spam the append-only history with near-duplicate snapshots.
    /// We snapshot when the canonical state (head/base/commits/reviewer state)
    /// changes; the latest dossier fields ride along with that snapshot.
    /// </remarks>
    /// <returns><c>true</c> if a snapshot was inserted, <c>false</c> if deduped.</returns>
    public async Task<bool> InsertIfChangedAsync(
        PrIdentity identity,
        DateTimeOffset syncedAt,
        string headSha,
        string baseSha,
        string? mergeBaseSha,
        IReadOnlyList<string> orderedCommitShas,
        ReviewerState? reviewerState,
        PullRequestStatus prState,
        string? rawMetadataJson,
        CancellationToken ct,
        string? mergeableState = null,
        string? ciStatus = null,
        IReadOnlyList<SnapshotFileChange>? files = null,
        string? reviewDecision = null)
    {
        await using var conn = await _db.OpenAsync(ct);

        var latest = await GetLatestAsync(conn, identity, ct);
        if (latest is not null &&
            latest.HeadSha == headSha &&
            latest.BaseSha == baseSha &&
            latest.MergeBaseSha == mergeBaseSha &&
            latest.PrState == prState &&
            latest.ReviewerState == reviewerState &&
            CommitShasEqual(latest.OrderedCommitShas, orderedCommitShas))
        {
            return false;
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO pr_snapshots (
              pr_identity, synced_at, head_sha, base_sha, merge_base_sha,
              ordered_commit_shas, reviewer_state, pr_state, raw_metadata_json,
              mergeable_state, ci_status, files_json, review_decision
            ) VALUES (
              $prId, $syncedAt, $headSha, $baseSha, $mergeBaseSha,
              $commitShas, $reviewerState, $prState, $rawJson,
              $mergeable, $ci, $files, $reviewDecision
            );
            """;
        cmd.Parameters.AddWithValue("$prId", identity.Url);
        cmd.Parameters.AddWithValue("$syncedAt", PullRequestRepository.FormatTimestamp(syncedAt));
        cmd.Parameters.AddWithValue("$headSha", headSha);
        cmd.Parameters.AddWithValue("$baseSha", baseSha);
        cmd.Parameters.AddWithValue("$mergeBaseSha", (object?)mergeBaseSha ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$commitShas", JsonSerializer.Serialize(orderedCommitShas));
        cmd.Parameters.AddWithValue("$reviewerState", (object?)reviewerState?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$prState", prState.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$rawJson", (object?)rawMetadataJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mergeable", (object?)mergeableState ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ci", (object?)ciStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$files",
            files is null || files.Count == 0
                ? DBNull.Value
                : (object)JsonSerializer.Serialize(files));
        cmd.Parameters.AddWithValue("$reviewDecision", (object?)reviewDecision ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
        return true;
    }

    /// <summary>
    /// Update the dossier metadata on the most recent snapshot for a PR
    /// without inserting a new history row. Used by the backfill / re-enrich
    /// path: when the canonical state hasn't changed (so dedup blocks an
    /// insert), we still want the latest snapshot's CI/mergeable/files to
    /// reflect the freshest fetch.
    /// </summary>
    /// <remarks>
    /// Consistent with <see cref="InsertIfChangedAsync"/>'s exclusion of these
    /// fields from the dedup comparison: the dossier metadata is "latest
    /// observation," not "canonical state at time T," so updating-in-place
    /// matches its semantics. Append-only history of canonical state is
    /// preserved.
    /// <para>
    /// Each non-null parameter overwrites the corresponding column. Nulls are
    /// left unchanged via COALESCE so a partial backfill never wipes a prior
    /// good value.
    /// </para>
    /// </remarks>
    public async Task UpdateLatestDossierAsync(
        PrIdentity identity,
        string? mergeableState,
        string? ciStatus,
        IReadOnlyList<SnapshotFileChange>? files,
        CancellationToken ct,
        string? reviewDecision = null)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE pr_snapshots
            SET mergeable_state = COALESCE($mergeable, mergeable_state),
                ci_status       = COALESCE($ci,        ci_status),
                files_json      = COALESCE($files,     files_json),
                review_decision = COALESCE($reviewDecision, review_decision)
            WHERE id = (
              SELECT id FROM pr_snapshots
              WHERE pr_identity = $prId
              ORDER BY synced_at DESC, id DESC
              LIMIT 1
            );
            """;
        cmd.Parameters.AddWithValue("$prId", identity.Url);
        cmd.Parameters.AddWithValue("$mergeable", (object?)mergeableState ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ci", (object?)ciStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$files",
            files is null || files.Count == 0
                ? DBNull.Value
                : (object)JsonSerializer.Serialize(files));
        cmd.Parameters.AddWithValue("$reviewDecision", (object?)reviewDecision ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Returns the most recent snapshot for a PR, or null if no snapshots exist.
    /// </summary>
    public async Task<PrSnapshotRow?> GetLatestAsync(PrIdentity identity, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await GetLatestAsync(conn, identity, ct);
    }

    private static async Task<PrSnapshotRow?> GetLatestAsync(SqliteConnection conn, PrIdentity identity, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM pr_snapshots
            WHERE pr_identity = $id
            ORDER BY synced_at DESC, id DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", identity.Url);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var commitShasJson = reader.GetString(reader.GetOrdinal("ordered_commit_shas"));
        var commitShas = JsonSerializer.Deserialize<List<string>>(commitShasJson) ?? new List<string>();

        return new PrSnapshotRow(
            Id: reader.GetInt64(reader.GetOrdinal("id")),
            Identity: new PrIdentity(
                Url: reader.GetString(reader.GetOrdinal("pr_identity")),
                Stable: identity.Stable),
            SyncedAt: DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("synced_at"))),
            HeadSha: reader.GetString(reader.GetOrdinal("head_sha")),
            BaseSha: reader.GetString(reader.GetOrdinal("base_sha")),
            MergeBaseSha: reader.IsDBNull(reader.GetOrdinal("merge_base_sha"))
                ? null
                : reader.GetString(reader.GetOrdinal("merge_base_sha")),
            OrderedCommitShas: commitShas,
            ReviewerState: reader.IsDBNull(reader.GetOrdinal("reviewer_state"))
                ? (ReviewerState?)null
                : Enum.Parse<ReviewerState>(reader.GetString(reader.GetOrdinal("reviewer_state"))),
            PrState: Enum.Parse<PullRequestStatus>(reader.GetString(reader.GetOrdinal("pr_state")), ignoreCase: true),
            RawMetadataJson: reader.IsDBNull(reader.GetOrdinal("raw_metadata_json"))
                ? null
                : reader.GetString(reader.GetOrdinal("raw_metadata_json")),
            MergeableState: ReadOptionalString(reader, "mergeable_state"),
            CiStatus: ReadOptionalString(reader, "ci_status"),
            Files: ReadOptionalFiles(reader, "files_json"),
            ReviewDecision: ReadOptionalString(reader, "review_decision"));
    }

    private static bool HasColumn(SqliteDataReader reader, string name)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string? ReadOptionalString(SqliteDataReader reader, string column)
    {
        if (!HasColumn(reader, column)) return null;
        var ord = reader.GetOrdinal(column);
        return reader.IsDBNull(ord) ? null : reader.GetString(ord);
    }

    private static IReadOnlyList<SnapshotFileChange>? ReadOptionalFiles(SqliteDataReader reader, string column)
    {
        var json = ReadOptionalString(reader, column);
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<List<SnapshotFileChange>>(json);
        }
        catch (JsonException)
        {
            // Tolerant of upgrade-time malformed rows; brief just falls back to "files unavailable".
            return null;
        }
    }

    private static bool CommitShasEqual(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }
}
