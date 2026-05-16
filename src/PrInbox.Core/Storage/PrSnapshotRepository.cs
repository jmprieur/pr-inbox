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
        CancellationToken ct)
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
              ordered_commit_shas, reviewer_state, pr_state, raw_metadata_json
            ) VALUES (
              $prId, $syncedAt, $headSha, $baseSha, $mergeBaseSha,
              $commitShas, $reviewerState, $prState, $rawJson
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

        await cmd.ExecuteNonQueryAsync(ct);
        return true;
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
                : reader.GetString(reader.GetOrdinal("raw_metadata_json")));
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
