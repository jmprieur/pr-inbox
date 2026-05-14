using System.Text.Json;
using Microsoft.Data.Sqlite;
using PrInbox.Core.Models;

namespace PrInbox.Core.Storage;

/// <summary>
/// Repository for <c>pull_requests</c>.
/// </summary>
public sealed class PullRequestRepository
{
    private readonly PrInboxDb _db;

    public PullRequestRepository(PrInboxDb db)
    {
        _db = db;
    }

    /// <summary>
    /// Insert a new pull_requests row or update the mutable fields of an
    /// existing one keyed by <c>stable_identity</c>. Preserves
    /// <c>first_seen_at</c> and tracking_reason on update.
    /// </summary>
    public async Task UpsertAsync(PullRequestRow row, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO pull_requests (
              pr_identity, stable_identity, source_id, source_kind,
              display_repo, number, title, author_login, url,
              status, tracking_reason, identity_used,
              first_seen_at, last_synced_at,
              last_briefed_head_sha, last_review_run_head_sha, last_posted_review_head_sha
            ) VALUES (
              $prId, $stableId, $sourceId, $sourceKind,
              $displayRepo, $number, $title, $author, $url,
              $status, $tracking, $identityUsed,
              $firstSeen, $lastSynced,
              $lastBriefed, $lastReviewRun, $lastPosted
            )
            ON CONFLICT(stable_identity) DO UPDATE SET
              pr_identity   = excluded.pr_identity,
              source_id     = excluded.source_id,
              source_kind   = excluded.source_kind,
              display_repo  = excluded.display_repo,
              number        = excluded.number,
              title         = excluded.title,
              author_login  = excluded.author_login,
              url           = excluded.url,
              status        = excluded.status,
              identity_used = excluded.identity_used,
              last_synced_at= excluded.last_synced_at;
            """;
        cmd.Parameters.AddWithValue("$prId", row.Identity.Display);
        cmd.Parameters.AddWithValue("$stableId", row.Identity.Stable);
        cmd.Parameters.AddWithValue("$sourceId", row.SourceId);
        cmd.Parameters.AddWithValue("$sourceKind", row.SourceKind.ToDbValue());
        cmd.Parameters.AddWithValue("$displayRepo", row.DisplayRepo);
        cmd.Parameters.AddWithValue("$number", row.Number);
        cmd.Parameters.AddWithValue("$title", (object?)row.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$author", (object?)row.AuthorLogin ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$url", row.Url);
        cmd.Parameters.AddWithValue("$status", row.Status.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$tracking", TrackingReasonToDb(row.TrackingReason));
        cmd.Parameters.AddWithValue("$identityUsed", row.IdentityUsed);
        cmd.Parameters.AddWithValue("$firstSeen", FormatTimestamp(row.FirstSeenAt));
        cmd.Parameters.AddWithValue("$lastSynced", FormatTimestamp(row.LastSyncedAt));
        cmd.Parameters.AddWithValue("$lastBriefed", (object?)row.LastBriefedHeadSha ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lastReviewRun", (object?)row.LastReviewRunHeadSha ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lastPosted", (object?)row.LastPostedReviewHeadSha ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Fetch a single row by display identity, or null if not found.
    /// </summary>
    public async Task<PullRequestRow?> GetAsync(string displayIdentity, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM pull_requests WHERE pr_identity = $id;";
        cmd.Parameters.AddWithValue("$id", displayIdentity);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapRow(reader) : null;
    }

    /// <summary>
    /// Returns all rows whose <c>tracking_reason</c> is <c>assigned</c> or
    /// <c>previously_assigned</c> and whose <c>status</c> is <c>open</c>.
    /// Used as the default scope for <c>pr-inbox list</c>.
    /// </summary>
    public async Task<IReadOnlyList<PullRequestRow>> ListActiveAsync(CancellationToken ct)
    {
        var rows = new List<PullRequestRow>();
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM pull_requests
            WHERE status = 'open'
              AND tracking_reason IN ('assigned','previously_assigned','manually_added')
            ORDER BY last_synced_at DESC;
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(MapRow(reader));
        }
        return rows;
    }

    /// <summary>
    /// Returns all rows, regardless of status / tracking reason. Used for
    /// <c>list --all</c>.
    /// </summary>
    public async Task<IReadOnlyList<PullRequestRow>> ListAllAsync(CancellationToken ct)
    {
        var rows = new List<PullRequestRow>();
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM pull_requests ORDER BY last_synced_at DESC;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(MapRow(reader));
        }
        return rows;
    }

    /// <summary>
    /// Mark a PR's <c>tracking_reason</c> to <c>previously_assigned</c>. Used
    /// when sync sees the PR is no longer in the inbox but the row already exists.
    /// </summary>
    public async Task MarkPreviouslyAssignedAsync(string displayIdentity, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE pull_requests
            SET tracking_reason = 'previously_assigned'
            WHERE pr_identity = $id AND tracking_reason = 'assigned';
            """;
        cmd.Parameters.AddWithValue("$id", displayIdentity);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Mark a PR as inaccessible (e.g. 404 / 403 after access change). Status only.
    /// </summary>
    public async Task MarkInaccessibleAsync(string displayIdentity, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE pull_requests
            SET status = 'inaccessible'
            WHERE pr_identity = $id;
            """;
        cmd.Parameters.AddWithValue("$id", displayIdentity);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Update only the <c>last_briefed_head_sha</c> column.
    /// </summary>
    public async Task UpdateLastBriefedAsync(string displayIdentity, string headSha, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE pull_requests SET last_briefed_head_sha = $sha WHERE pr_identity = $id;";
        cmd.Parameters.AddWithValue("$sha", headSha);
        cmd.Parameters.AddWithValue("$id", displayIdentity);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    internal static PullRequestRow MapRow(SqliteDataReader reader)
    {
        return new PullRequestRow(
            Identity: new PrIdentity(
                Display: reader.GetString(reader.GetOrdinal("pr_identity")),
                Stable: reader.GetString(reader.GetOrdinal("stable_identity"))),
            SourceKind: SourceKindExtensions.FromDbValue(reader.GetString(reader.GetOrdinal("source_kind"))),
            SourceId: reader.GetString(reader.GetOrdinal("source_id")),
            DisplayRepo: reader.GetString(reader.GetOrdinal("display_repo")),
            Number: reader.GetInt32(reader.GetOrdinal("number")),
            Title: GetStringOrNull(reader, "title"),
            AuthorLogin: GetStringOrNull(reader, "author_login"),
            Url: reader.GetString(reader.GetOrdinal("url")),
            Status: ParsePullRequestStatus(reader.GetString(reader.GetOrdinal("status"))),
            TrackingReason: ParseTrackingReason(reader.GetString(reader.GetOrdinal("tracking_reason"))),
            IdentityUsed: reader.GetString(reader.GetOrdinal("identity_used")),
            FirstSeenAt: DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("first_seen_at"))),
            LastSyncedAt: DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("last_synced_at"))),
            LastBriefedHeadSha: GetStringOrNull(reader, "last_briefed_head_sha"),
            LastReviewRunHeadSha: GetStringOrNull(reader, "last_review_run_head_sha"),
            LastPostedReviewHeadSha: GetStringOrNull(reader, "last_posted_review_head_sha"));
    }

    internal static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

    private static string? GetStringOrNull(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static PullRequestStatus ParsePullRequestStatus(string value) => value switch
    {
        "open" => PullRequestStatus.Open,
        "closed" => PullRequestStatus.Closed,
        "merged" => PullRequestStatus.Merged,
        "inaccessible" => PullRequestStatus.Inaccessible,
        _ => throw new InvalidOperationException($"Unknown PR status '{value}'."),
    };

    private static TrackingReason ParseTrackingReason(string value) => value switch
    {
        "assigned" => TrackingReason.Assigned,
        "previously_assigned" => TrackingReason.PreviouslyAssigned,
        "manually_added" => TrackingReason.ManuallyAdded,
        "archived" => TrackingReason.Archived,
        _ => throw new InvalidOperationException($"Unknown tracking_reason '{value}'."),
    };

    internal static string TrackingReasonToDb(TrackingReason reason) => reason switch
    {
        TrackingReason.Assigned => "assigned",
        TrackingReason.PreviouslyAssigned => "previously_assigned",
        TrackingReason.ManuallyAdded => "manually_added",
        TrackingReason.Archived => "archived",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };
}
