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
              first_seen_at, last_synced_at, enrich_state,
              last_briefed_head_sha, last_review_run_head_sha, last_posted_review_head_sha,
              body, last_upstream_updated_at
            ) VALUES (
              $prId, $stableId, $sourceId, $sourceKind,
              $displayRepo, $number, $title, $author, $url,
              $status, $tracking, $identityUsed,
              $firstSeen, $lastSynced, $enrichState,
              $lastBriefed, $lastReviewRun, $lastPosted,
              $body, $lastUpstreamUpdated
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
              last_synced_at= excluded.last_synced_at,
              enrich_state  = excluded.enrich_state,
              -- Preserve a previously enriched body across subsequent
              -- fast-pass upserts (which carry body=null).
              body          = COALESCE(excluded.body, pull_requests.body),
              -- Always overwrite the upstream updated-at: fast-sync has
              -- the freshest value (or NULL on adapters that don't
              -- supply one — we still want to clear stale values).
              last_upstream_updated_at = excluded.last_upstream_updated_at;

            -- Record this (source, identity) binding for the PR. Idempotent:
            -- the first sync that discovered the PR seeded one row in
            -- migration 002; subsequent discoveries by other identities
            -- accumulate more rows without affecting the main row.
            INSERT OR IGNORE INTO pr_source_bindings (
              pr_identity, source_id, identity_used, discovered_at
            ) VALUES (
              $prId, $sourceId, $identityUsed, $lastSynced
            );
            """;
        cmd.Parameters.AddWithValue("$prId", row.Identity.Url);
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
        cmd.Parameters.AddWithValue("$enrichState", row.EnrichState.ToDbValue());
        cmd.Parameters.AddWithValue("$lastBriefed", (object?)row.LastBriefedHeadSha ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lastReviewRun", (object?)row.LastReviewRunHeadSha ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lastPosted", (object?)row.LastPostedReviewHeadSha ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$body", (object?)row.Body ?? DBNull.Value);
        cmd.Parameters.AddWithValue(
            "$lastUpstreamUpdated",
            row.LastUpstreamUpdatedAt is null
                ? DBNull.Value
                : (object)FormatTimestamp(row.LastUpstreamUpdatedAt.Value));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Update only the <c>body</c> column. Called from the enrich path so the
    /// brief can render "Author's framing" without re-issuing a full upsert.
    /// Passing <c>null</c> clears the column.
    /// </summary>
    public async Task UpdateBodyAsync(string url, string? body, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE pull_requests SET body = $b WHERE pr_identity = $id;";
        cmd.Parameters.AddWithValue("$b", (object?)body ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", url);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Stamp the dossier schema version this PR was last enriched against.
    /// The enrich pass calls this with <see cref="BriefService.CurrentDossierVersion"/>
    /// after a successful enrich; the next dossier-schema bump (migration 008,
    /// 009, …) automatically re-elects every row for backfill.
    /// </summary>
    public async Task UpdateDossierVersionAsync(string url, int version, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE pull_requests SET dossier_version = $v WHERE pr_identity = $id;";
        cmd.Parameters.AddWithValue("$v", version);
        cmd.Parameters.AddWithValue("$id", url);
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

    /// <summary>
    /// List PRs that need a tier-3 enrichment pass.
    /// </summary>
    /// <remarks>
    /// A PR is a candidate when, viewed through the binding (<paramref name="sourceId"/>,
    /// <paramref name="identityUsed"/>), it is open, owned by the user, not
    /// ignored, and either:
    /// <list type="bullet">
    ///   <item><c>enrich_state = 'basic'</c> — has never been enriched.</item>
    ///   <item><c>dossier_version &lt; <paramref name="minDossierVersion"/></c> — was
    ///         enriched against an older brief contract; a one-shot backfill
    ///         is needed to fill the new dossier columns (body/files/CI/...).
    ///         </item>
    /// </list>
    /// Basic rows are returned first so a fresh PR never starves behind a
    /// large backfill queue.
    /// </remarks>
    public async Task<IReadOnlyList<PullRequestRow>> ListNeedingEnrichmentAsync(
        string sourceId, string identityUsed, int minDossierVersion, CancellationToken ct)
    {
        var rows = new List<PullRequestRow>();
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT pr.*
            FROM pull_requests pr
            INNER JOIN pr_source_bindings b
              ON b.pr_identity = pr.pr_identity
             AND b.source_id   = $sourceId
             AND b.identity_used = $identityUsed
            WHERE pr.status          = 'open'
              AND pr.tracking_reason IN ('assigned','manually_added')
              AND pr.is_ignored      = 0
              AND (
                pr.enrich_state = 'basic'
                OR pr.dossier_version < $minVersion
              )
            ORDER BY CASE WHEN pr.enrich_state = 'basic' THEN 0 ELSE 1 END ASC,
                     pr.last_synced_at DESC;
            """;
        cmd.Parameters.AddWithValue("$sourceId", sourceId);
        cmd.Parameters.AddWithValue("$identityUsed", identityUsed);
        cmd.Parameters.AddWithValue("$minVersion", minDossierVersion);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(MapRow(reader));
        }
        return rows;
    }

    /// <summary>
    /// Mark a PR's <c>enrich_state</c> as <c>enriched</c>. Called by the
    /// orchestrator after a successful tier-3 enrichment has persisted the
    /// detail snapshot and threads.
    /// </summary>
    public async Task MarkEnrichedAsync(string url, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE pull_requests
            SET enrich_state = 'enriched'
            WHERE pr_identity = $id;
            """;
        cmd.Parameters.AddWithValue("$id", url);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Set or clear the per-PR ignore flag. UI-only filter; does not
    /// affect sync. <c>true</c> hides the row from the inbox by default;
    /// the user can flip the toolbar "Show ignored" toggle to reveal.
    /// </summary>
    public async Task SetIgnoredAsync(string url, bool ignored, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE pull_requests SET is_ignored = $v WHERE pr_identity = $id;";
        cmd.Parameters.AddWithValue("$v", ignored ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", url);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Mark a PR as "done" at the given head SHA. The inbox hides rows
    /// where <c>marked_done_head_sha</c> equals the current head SHA from
    /// the latest snapshot — i.e. the author hasn't pushed since the user
    /// marked it done. Calling this for the same PR twice is idempotent;
    /// calling it with a different SHA updates the marker.
    /// </summary>
    public async Task MarkDoneAsync(string url, string headSha, DateTimeOffset markedAt, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE pull_requests
            SET marked_done_head_sha = $sha,
                marked_done_at       = $at
            WHERE pr_identity = $id;
            """;
        cmd.Parameters.AddWithValue("$sha", headSha);
        cmd.Parameters.AddWithValue("$at", FormatTimestamp(markedAt));
        cmd.Parameters.AddWithValue("$id", url);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Clear the "done" marker, returning the row to its un-snoozed state.
    /// </summary>
    public async Task ClearDoneAsync(string url, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE pull_requests
            SET marked_done_head_sha = NULL,
                marked_done_at       = NULL
            WHERE pr_identity = $id;
            """;
        cmd.Parameters.AddWithValue("$id", url);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Flag a PR as "of interest." A flagged row gets a visible ⭐ marker
    /// and can be isolated via the inbox "Show only flagged" filter.
    /// Orthogonal to done / ignore / closed: flagging does not bypass
    /// any other filter. Idempotent — calling twice keeps the original
    /// <paramref name="flaggedAt"/> if the caller passes the same value.
    /// </summary>
    public async Task FlagAsync(string url, DateTimeOffset flaggedAt, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE pull_requests
            SET flagged_at = $at
            WHERE pr_identity = $id;
            """;
        cmd.Parameters.AddWithValue("$at", FormatTimestamp(flaggedAt));
        cmd.Parameters.AddWithValue("$id", url);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Clear the "of interest" flag.
    /// </summary>
    public async Task UnflagAsync(string url, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE pull_requests
            SET flagged_at = NULL
            WHERE pr_identity = $id;
            """;
        cmd.Parameters.AddWithValue("$id", url);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Update <c>pull_requests.status</c>. Used by the enrich path so a PR
    /// that has been merged/closed since the last fast pass surfaces its
    /// new state in the UI instead of remaining stuck at <c>open</c>.
    /// </summary>
    public async Task UpdateStatusAsync(string url, PullRequestStatus status, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE pull_requests SET status = $s WHERE pr_identity = $id;";
        cmd.Parameters.AddWithValue("$s", status.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$id", url);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Set or clear <c>disappeared_at</c>. The disappeared-diff sweep
    /// stamps this when a PR drops out of the fast-sync result list yet
    /// the per-PR re-enrich reports <c>status='open'</c> — i.e. the user
    /// is no longer a requested reviewer (or the search criteria moved
    /// on). Pass <c>null</c> to clear (e.g. when the PR reappears in a
    /// later fast pass).
    /// </summary>
    public async Task SetDisappearedAtAsync(string url, DateTimeOffset? when, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE pull_requests SET disappeared_at = $w WHERE pr_identity = $id;";
        cmd.Parameters.AddWithValue("$w", when is null ? DBNull.Value : (object)FormatTimestamp(when.Value));
        cmd.Parameters.AddWithValue("$id", url);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Stamp <c>last_swept_at</c>. The TTL sweep updates this every time
    /// it re-enriches a row so the next cycle picks a different oldest
    /// candidate.
    /// </summary>
    public async Task MarkSweptAsync(string url, DateTimeOffset when, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE pull_requests SET last_swept_at = $w WHERE pr_identity = $id;";
        cmd.Parameters.AddWithValue("$w", FormatTimestamp(when));
        cmd.Parameters.AddWithValue("$id", url);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Returns the URLs of every <c>status='open'</c> row that the given
    /// (source, identity) binding is authorized to see. Used by the
    /// disappeared-diff sweep to compute <c>db_open - returned_urls</c>.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListOpenUrlsByIdentityAsync(
        string sourceId, string identityUsed, CancellationToken ct)
    {
        var result = new List<string>();
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT pr.pr_identity
            FROM pull_requests pr
            INNER JOIN pr_source_bindings b
              ON b.pr_identity   = pr.pr_identity
             AND b.source_id     = $sourceId
             AND b.identity_used = $identityUsed
            WHERE pr.status = 'open';
            """;
        cmd.Parameters.AddWithValue("$sourceId", sourceId);
        cmd.Parameters.AddWithValue("$identityUsed", identityUsed);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    /// <summary>
    /// Returns up to <paramref name="limit"/> open, non-ignored rows that
    /// this (source, identity) binding owns, ordered by the oldest sweep
    /// timestamp first (rows never swept come before rows recently
    /// swept). Used by the TTL sweep to verify that PRs we believe are
    /// open really still are.
    /// </summary>
    public async Task<IReadOnlyList<PullRequestRow>> ListOldestSweptOpenAsync(
        string sourceId, string identityUsed, int limit, CancellationToken ct)
    {
        var rows = new List<PullRequestRow>();
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT pr.*
            FROM pull_requests pr
            INNER JOIN pr_source_bindings b
              ON b.pr_identity   = pr.pr_identity
             AND b.source_id     = $sourceId
             AND b.identity_used = $identityUsed
            WHERE pr.status     = 'open'
              AND pr.is_ignored = 0
            ORDER BY COALESCE(pr.last_swept_at, '0001-01-01T00:00:00Z') ASC,
                     pr.last_synced_at ASC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$sourceId", sourceId);
        cmd.Parameters.AddWithValue("$identityUsed", identityUsed);
        cmd.Parameters.AddWithValue("$limit", limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(MapRow(reader));
        }
        return rows;
    }

    internal static PullRequestRow MapRow(SqliteDataReader reader)
    {
        return new PullRequestRow(
            Identity: new PrIdentity(
                Url: reader.GetString(reader.GetOrdinal("pr_identity")),
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
            EnrichState: EnrichStateExtensions.FromDbValue(reader.GetString(reader.GetOrdinal("enrich_state"))),
            LastBriefedHeadSha: GetStringOrNull(reader, "last_briefed_head_sha"),
            LastReviewRunHeadSha: GetStringOrNull(reader, "last_review_run_head_sha"),
            LastPostedReviewHeadSha: GetStringOrNull(reader, "last_posted_review_head_sha"),
            IsIgnored: HasColumn(reader, "is_ignored") && reader.GetInt64(reader.GetOrdinal("is_ignored")) != 0,
            DisappearedAt: ParseOptionalTimestamp(reader, "disappeared_at"),
            LastSweptAt: ParseOptionalTimestamp(reader, "last_swept_at"),
            Body: HasColumn(reader, "body") && !reader.IsDBNull(reader.GetOrdinal("body"))
                ? reader.GetString(reader.GetOrdinal("body"))
                : null,
            DossierVersion: HasColumn(reader, "dossier_version")
                ? (int)reader.GetInt64(reader.GetOrdinal("dossier_version"))
                : 0,
            LastUpstreamUpdatedAt: ParseOptionalTimestamp(reader, "last_upstream_updated_at"),
            MarkedDoneHeadSha: HasColumn(reader, "marked_done_head_sha")
                ? GetStringOrNull(reader, "marked_done_head_sha")
                : null,
            MarkedDoneAt: ParseOptionalTimestamp(reader, "marked_done_at"),
            FlaggedAt: ParseOptionalTimestamp(reader, "flagged_at"));
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

    private static DateTimeOffset? ParseOptionalTimestamp(SqliteDataReader reader, string column)
    {
        if (!HasColumn(reader, column)) return null;
        var ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal)) return null;
        return DateTimeOffset.Parse(reader.GetString(ordinal));
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
