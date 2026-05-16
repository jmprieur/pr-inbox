using System.Text.Json;
using Microsoft.Data.Sqlite;
using PrInbox.Core.Models;

namespace PrInbox.Core.Storage;

/// <summary>
/// Repository for <c>posted_reviews</c>. Inserted by the publisher project
/// after a successful live POST to the platform; read by the publisher and
/// by the web UI for idempotency and "this finding was already posted"
/// indicators.
/// </summary>
/// <remarks>
/// The table column historically named <c>pr_identity</c> stores PR URLs
/// after migration v2. <c>finding_ids_json</c> and
/// <c>finding_fingerprints_json</c> (migration v4) hold the per-finding
/// idempotency keys.
/// </remarks>
public sealed class PostedReviewRepository
{
    private readonly PrInboxDb _db;

    public PostedReviewRepository(PrInboxDb db) => _db = db;

    /// <summary>
    /// Insert a row recording a successful (live) post. Returns the row id.
    /// Dry-run publishes MUST NOT call this; they leave the table untouched.
    /// </summary>
    public async Task<long> InsertAsync(
        PrIdentity identity,
        long? reviewRunId,
        string platformReviewId,
        string? reviewUrl,
        DateTimeOffset postedAt,
        string headShaAtPost,
        string identityUsed,
        int inlineCount,
        bool bodyPresent,
        IReadOnlyList<string> findingIds,
        IReadOnlyList<string> findingFingerprints,
        CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO posted_reviews (
              pr_identity, review_run_id, platform_review_id, review_url,
              posted_at, head_sha_at_post, identity_used,
              inline_count, body_present,
              finding_ids_json, finding_fingerprints_json, dry_run
            ) VALUES (
              $prId, $runId, $reviewId, $url,
              $postedAt, $headSha, $identityUsed,
              $inline, $body,
              $idsJson, $fpJson, 0
            )
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$prId", identity.Url);
        cmd.Parameters.AddWithValue("$runId", (object?)reviewRunId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$reviewId", platformReviewId);
        cmd.Parameters.AddWithValue("$url", (object?)reviewUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$postedAt", PullRequestRepository.FormatTimestamp(postedAt));
        cmd.Parameters.AddWithValue("$headSha", headShaAtPost);
        cmd.Parameters.AddWithValue("$identityUsed", identityUsed);
        cmd.Parameters.AddWithValue("$inline", inlineCount);
        cmd.Parameters.AddWithValue("$body", bodyPresent ? 1 : 0);
        cmd.Parameters.AddWithValue("$idsJson", JsonSerializer.Serialize(findingIds));
        cmd.Parameters.AddWithValue("$fpJson", JsonSerializer.Serialize(findingFingerprints));

        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result);
    }

    /// <summary>
    /// Returns all posted-review rows for a PR, newest-first.
    /// </summary>
    public async Task<IReadOnlyList<PostedReviewRow>> ListForPrAsync(
        PrIdentity identity,
        CancellationToken ct)
    {
        var rows = new List<PostedReviewRow>();
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT pr.stable_identity, pr_reviews.*
            FROM posted_reviews pr_reviews
            JOIN pull_requests pr ON pr.pr_identity = pr_reviews.pr_identity
            WHERE pr_reviews.pr_identity = $id
            ORDER BY pr_reviews.posted_at DESC, pr_reviews.id DESC;
            """;
        cmd.Parameters.AddWithValue("$id", identity.Url);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(MapRow(reader));
        }
        return rows;
    }

    /// <summary>
    /// Return the set of finding ids and fingerprints that have already been
    /// posted for this PR (across all runs). The publisher uses this to skip
    /// findings whose id or fingerprint is already present.
    /// </summary>
    public async Task<(HashSet<string> Ids, HashSet<string> Fingerprints)> GetPostedFindingsForPrAsync(
        PrIdentity identity,
        CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var fps = new HashSet<string>(StringComparer.Ordinal);

        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT finding_ids_json, finding_fingerprints_json
            FROM posted_reviews
            WHERE pr_identity = $id;
            """;
        cmd.Parameters.AddWithValue("$id", identity.Url);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            AddJsonArrayInto(reader.GetString(0), ids);
            AddJsonArrayInto(reader.GetString(1), fps);
        }
        return (ids, fps);
    }

    private static void AddJsonArrayInto(string json, HashSet<string> bucket)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var arr = JsonSerializer.Deserialize<string[]>(json);
            if (arr is null) return;
            foreach (var s in arr) if (!string.IsNullOrEmpty(s)) bucket.Add(s);
        }
        catch (JsonException)
        {
            // Malformed historical row — ignore; treat as no prior posts.
        }
    }

    private static PostedReviewRow MapRow(SqliteDataReader reader)
    {
        var url = reader.GetString(reader.GetOrdinal("pr_identity"));
        var stable = reader.GetString(reader.GetOrdinal("stable_identity"));
        var idsRaw = reader.GetString(reader.GetOrdinal("finding_ids_json"));
        var fpsRaw = reader.GetString(reader.GetOrdinal("finding_fingerprints_json"));

        return new PostedReviewRow(
            Id: reader.GetInt64(reader.GetOrdinal("id")),
            Identity: new PrIdentity(url, stable),
            ReviewRunId: reader.IsDBNull(reader.GetOrdinal("review_run_id"))
                ? null
                : reader.GetInt64(reader.GetOrdinal("review_run_id")),
            PlatformReviewId: reader.GetString(reader.GetOrdinal("platform_review_id")),
            ReviewUrl: reader.IsDBNull(reader.GetOrdinal("review_url"))
                ? null
                : reader.GetString(reader.GetOrdinal("review_url")),
            PostedAt: DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("posted_at"))),
            HeadShaAtPost: reader.GetString(reader.GetOrdinal("head_sha_at_post")),
            IdentityUsed: reader.GetString(reader.GetOrdinal("identity_used")),
            InlineCount: reader.GetInt32(reader.GetOrdinal("inline_count")),
            BodyPresent: reader.GetInt32(reader.GetOrdinal("body_present")) != 0,
            FindingIds: ParseJsonArray(idsRaw),
            FindingFingerprints: ParseJsonArray(fpsRaw),
            DryRun: reader.GetInt32(reader.GetOrdinal("dry_run")) != 0);
    }

    private static IReadOnlyList<string> ParseJsonArray(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }
}
