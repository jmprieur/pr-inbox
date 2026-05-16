using Microsoft.Data.Sqlite;
using PrInbox.Core.Models;

namespace PrInbox.Core.Storage;

/// <summary>
/// Repository for <c>observed_threads</c>. Threads are upserted by
/// (pr_identity, platform_thread_id); first_seen_at is preserved, last_seen_at
/// is moved forward, resolved_at is set when newly resolved.
/// </summary>
public sealed class ObservedThreadRepository
{
    private readonly PrInboxDb _db;

    public ObservedThreadRepository(PrInboxDb db) => _db = db;

    /// <summary>
    /// Upsert a batch of threads observed during a sync. Inserts new threads,
    /// bumps <c>last_seen_at</c> on existing ones, and sets <c>resolved_at</c>
    /// when a thread transitions to resolved.
    /// </summary>
    public async Task UpsertManyAsync(
        PrIdentity identity,
        IReadOnlyList<RemoteThread> threads,
        DateTimeOffset syncedAt,
        CancellationToken ct)
    {
        if (threads.Count == 0) return;

        await using var conn = await _db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        try
        {
            foreach (var thread in threads)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO observed_threads (
                      pr_identity, platform_thread_id, kind, author_login,
                      is_bot, bot_kind,
                      first_seen_at, last_seen_at, resolved_at, raw_json,
                      last_comment_body, anchor_path, anchor_line
                    ) VALUES (
                      $prId, $threadId, $kind, $author,
                      $isBot, $botKind,
                      $syncedAt, $syncedAt, $resolvedAt, $rawJson,
                      $body, $path, $line
                    )
                    ON CONFLICT(pr_identity, platform_thread_id) DO UPDATE SET
                      kind              = excluded.kind,
                      author_login      = excluded.author_login,
                      is_bot            = excluded.is_bot,
                      bot_kind          = excluded.bot_kind,
                      last_seen_at      = excluded.last_seen_at,
                      resolved_at       = COALESCE(observed_threads.resolved_at, excluded.resolved_at),
                      raw_json          = excluded.raw_json,
                      last_comment_body = COALESCE(excluded.last_comment_body, observed_threads.last_comment_body),
                      anchor_path       = COALESCE(excluded.anchor_path, observed_threads.anchor_path),
                      anchor_line       = COALESCE(excluded.anchor_line, observed_threads.anchor_line);
                    """;
                cmd.Parameters.AddWithValue("$prId", identity.Url);
                cmd.Parameters.AddWithValue("$threadId", thread.PlatformThreadId);
                cmd.Parameters.AddWithValue("$kind", thread.Kind.ToString());
                cmd.Parameters.AddWithValue("$author", (object?)thread.AuthorLogin ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$isBot", thread.IsBot ? 1 : 0);
                cmd.Parameters.AddWithValue("$botKind", (object?)thread.BotKind?.ToString() ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$syncedAt",
                    PullRequestRepository.FormatTimestamp(syncedAt));
                cmd.Parameters.AddWithValue("$resolvedAt",
                    thread.IsResolved
                        ? (object)PullRequestRepository.FormatTimestamp(syncedAt)
                        : DBNull.Value);
                cmd.Parameters.AddWithValue("$rawJson", (object?)thread.RawJson ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$body", (object?)thread.BodyExcerpt ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$path", (object?)thread.AnchorPath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$line",
                    thread.AnchorLine.HasValue ? (object)thread.AnchorLine.Value : DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// Returns all unresolved threads for a PR, used by the review brief.
    /// </summary>
    public async Task<IReadOnlyList<ObservedThreadRow>> GetOpenThreadsAsync(PrIdentity identity, CancellationToken ct)
    {
        var rows = new List<ObservedThreadRow>();
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM observed_threads
            WHERE pr_identity = $id AND resolved_at IS NULL
            ORDER BY first_seen_at ASC;
            """;
        cmd.Parameters.AddWithValue("$id", identity.Url);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(MapRow(reader, identity));
        }
        return rows;
    }

    /// <summary>
    /// Returns all bot threads first seen after <paramref name="since"/>.
    /// Used by the review brief to surface "new Copilot comments since last brief."
    /// </summary>
    public async Task<IReadOnlyList<ObservedThreadRow>> GetBotThreadsSinceAsync(
        PrIdentity identity,
        DateTimeOffset since,
        CancellationToken ct)
    {
        var rows = new List<ObservedThreadRow>();
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM observed_threads
            WHERE pr_identity = $id AND is_bot = 1 AND first_seen_at >= $since
            ORDER BY first_seen_at ASC;
            """;
        cmd.Parameters.AddWithValue("$id", identity.Url);
        cmd.Parameters.AddWithValue("$since",
            PullRequestRepository.FormatTimestamp(since));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(MapRow(reader, identity));
        }
        return rows;
    }

    private static ObservedThreadRow MapRow(SqliteDataReader reader, PrIdentity identity)
    {
        return new ObservedThreadRow(
            Id: reader.GetInt64(reader.GetOrdinal("id")),
            Identity: identity,
            PlatformThreadId: reader.GetString(reader.GetOrdinal("platform_thread_id")),
            Kind: Enum.Parse<ThreadKind>(reader.GetString(reader.GetOrdinal("kind"))),
            AuthorLogin: reader.IsDBNull(reader.GetOrdinal("author_login"))
                ? null
                : reader.GetString(reader.GetOrdinal("author_login")),
            IsBot: reader.GetInt32(reader.GetOrdinal("is_bot")) != 0,
            BotKind: reader.IsDBNull(reader.GetOrdinal("bot_kind"))
                ? null
                : Enum.Parse<BotKind>(reader.GetString(reader.GetOrdinal("bot_kind"))),
            FirstSeenAt: DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("first_seen_at"))),
            LastSeenAt: DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("last_seen_at"))),
            ResolvedAt: reader.IsDBNull(reader.GetOrdinal("resolved_at"))
                ? null
                : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("resolved_at"))),
            RawJson: reader.IsDBNull(reader.GetOrdinal("raw_json"))
                ? null
                : reader.GetString(reader.GetOrdinal("raw_json")),
            LastCommentBody: ReadOptionalString(reader, "last_comment_body"),
            AnchorPath: ReadOptionalString(reader, "anchor_path"),
            AnchorLine: ReadOptionalInt(reader, "anchor_line"));
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

    private static int? ReadOptionalInt(SqliteDataReader reader, string column)
    {
        if (!HasColumn(reader, column)) return null;
        var ord = reader.GetOrdinal(column);
        return reader.IsDBNull(ord) ? null : (int)reader.GetInt64(ord);
    }
}
