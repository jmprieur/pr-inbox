using Microsoft.Data.Sqlite;

namespace PrInbox.Core.Storage;

/// <summary>
/// A row in the <c>tags</c> table. Tag names are stored case-insensitive
/// (COLLATE NOCASE), so callers can pass any casing and lookups match.
/// </summary>
public sealed record TagRow(string Name, string Color, DateTimeOffset CreatedAt);

/// <summary>
/// Repository for user-defined tags and their N:M link to PRs.
///
/// Design notes:
/// <list type="bullet">
///   <item><b>Global.</b> Tags are not partitioned by identity; the same
///         tag dictionary applies to every PR the user sees, regardless of
///         which account it was fetched under.</item>
///   <item><b>Case-insensitive names.</b> SQLite's <c>COLLATE NOCASE</c>
///         on the primary key handles this — "Security" and "security"
///         are the same tag.</item>
///   <item><b>Cascading deletes.</b> The <c>pr_tags</c> FK uses
///         <c>ON DELETE CASCADE ON UPDATE CASCADE</c>, so deleting or
///         renaming a tag automatically maintains the join table.</item>
/// </list>
/// </summary>
public sealed class TagRepository
{
    private readonly PrInboxDb _db;

    public TagRepository(PrInboxDb db)
    {
        _db = db;
    }

    // -- Tag dictionary -------------------------------------------------

    /// <summary>
    /// Create a tag if no tag with the same (case-insensitive) name
    /// already exists. Idempotent: a second call with the same name is
    /// a no-op (the existing color is preserved).
    /// </summary>
    public async Task CreateTagAsync(string name, string color, DateTimeOffset createdAt, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(color);
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tags (name, color, created_at)
            VALUES ($name, $color, $createdAt)
            ON CONFLICT(name) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$name", name.Trim());
        cmd.Parameters.AddWithValue("$color", color);
        cmd.Parameters.AddWithValue("$createdAt", FormatTimestamp(createdAt));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Update the color of an existing tag. No-op if the tag does not
    /// exist (caller should create it first if needed).
    /// </summary>
    public async Task SetTagColorAsync(string name, string color, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(color);
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE tags SET color = $color WHERE name = $name;";
        cmd.Parameters.AddWithValue("$name", name.Trim());
        cmd.Parameters.AddWithValue("$color", color);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Delete a tag. Cascade rules drop every matching row in
    /// <c>pr_tags</c> as part of the same statement.
    /// </summary>
    public async Task DeleteTagAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM tags WHERE name = $name;";
        cmd.Parameters.AddWithValue("$name", name.Trim());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Returns every tag in creation order (oldest first). The caller can
    /// re-sort by name in the UI if desired.
    /// </summary>
    public async Task<IReadOnlyList<TagRow>> ListTagsAsync(CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, color, created_at FROM tags ORDER BY created_at, name;";
        var result = new List<TagRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new TagRow(
                reader.GetString(0),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2))));
        }
        return result;
    }

    // -- PR <-> tag links ----------------------------------------------

    /// <summary>
    /// Attach a tag to a PR. Idempotent: if the link exists the original
    /// <paramref name="addedAt"/> is kept (the conflict clause does nothing).
    /// Fails (FK violation) if the tag does not exist — callers must call
    /// <see cref="CreateTagAsync"/> first.
    /// </summary>
    public async Task AddTagToPrAsync(string prUrl, string tagName, DateTimeOffset addedAt, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(tagName);
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO pr_tags (pr_url, tag_name, added_at)
            VALUES ($url, $tag, $at)
            ON CONFLICT(pr_url, tag_name) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$url", prUrl);
        cmd.Parameters.AddWithValue("$tag", tagName.Trim());
        cmd.Parameters.AddWithValue("$at", FormatTimestamp(addedAt));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Detach a tag from a PR. No-op if the link does not exist.
    /// </summary>
    public async Task RemoveTagFromPrAsync(string prUrl, string tagName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(tagName);
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM pr_tags WHERE pr_url = $url AND tag_name = $tag;";
        cmd.Parameters.AddWithValue("$url", prUrl);
        cmd.Parameters.AddWithValue("$tag", tagName.Trim());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// All tags attached to a single PR, in the order they were added.
    /// </summary>
    public async Task<IReadOnlyList<TagRow>> GetTagsForPrAsync(string prUrl, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prUrl);
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT t.name, t.color, t.created_at
            FROM pr_tags pt
            JOIN tags t ON t.name = pt.tag_name
            WHERE pt.pr_url = $url
            ORDER BY pt.added_at, t.name;
            """;
        cmd.Parameters.AddWithValue("$url", prUrl);
        var result = new List<TagRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new TagRow(
                reader.GetString(0),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2))));
        }
        return result;
    }

    /// <summary>
    /// Bulk read: every PR's tags in one round-trip, keyed by PR URL.
    /// Used by the inbox to plumb tags onto rows without N+1 queries.
    /// PRs with no tags are absent from the result (caller should default
    /// to empty list when the URL is missing).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<TagRow>>> GetAllPrTagsAsync(CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT pt.pr_url, t.name, t.color, t.created_at
            FROM pr_tags pt
            JOIN tags t ON t.name = pt.tag_name
            ORDER BY pt.pr_url, pt.added_at, t.name;
            """;
        var working = new Dictionary<string, List<TagRow>>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var url = reader.GetString(0);
            var tag = new TagRow(
                reader.GetString(1),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3)));
            if (!working.TryGetValue(url, out var list))
            {
                list = new List<TagRow>();
                working[url] = list;
            }
            list.Add(tag);
        }
        // Cast each List<TagRow> to IReadOnlyList<TagRow> for the public surface.
        var result = new Dictionary<string, IReadOnlyList<TagRow>>(working.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in working) result[kvp.Key] = kvp.Value;
        return result;
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
}
