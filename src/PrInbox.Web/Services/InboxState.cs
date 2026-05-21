using PrInbox.Core.Models;
using PrInbox.Core.Storage;

namespace PrInbox.Web.Services;

/// <summary>
/// Live PR-row plus presentation metadata. Decouples Blazor components
/// from the <see cref="PullRequestRow"/> shape so the row can be
/// progressively enriched on the server and pushed to the client.
/// </summary>
public sealed record InboxRow(
    string Url,
    string DisplayRepo,
    int Number,
    string? Title,
    string? AuthorLogin,
    string SourceId,
    SourceKind SourceKind,
    string IdentityUsed,
    PullRequestStatus Status,
    EnrichState EnrichState,
    DateTimeOffset LastSyncedAt,
    int OpenThreadCount,
    int UnresolvedBotCount,
    DriftKind DriftKind,
    int DriftCount,
    string? LastReviewedHeadSha,
    string? CurrentHeadSha,
    bool IsIgnored = false,
    DateTimeOffset? DisappearedAt = null,
    int LikelyDoneCount = 0,
    DateTimeOffset? LastUpstreamUpdatedAt = null,
    string? MarkedDoneHeadSha = null,
    DateTimeOffset? MarkedDoneAt = null)
{
    /// <summary>
    /// True when the user marked this PR done and the author has NOT
    /// pushed since (or the current head SHA is unknown — defensive: we
    /// don't yank a "done" badge just because a snapshot hasn't landed).
    /// Hidden by default; revealed by the inbox "Show done" toggle.
    /// </summary>
    public bool IsMarkedDone =>
        !string.IsNullOrEmpty(MarkedDoneHeadSha)
        && (string.IsNullOrEmpty(CurrentHeadSha)
            || string.Equals(MarkedDoneHeadSha, CurrentHeadSha, StringComparison.Ordinal));

    /// <summary>
    /// True when the user marked the PR done at an older SHA and the
    /// author has since pushed. Drives the "Updated since you marked
    /// done" chip so the user understands why the row reappeared.
    /// </summary>
    public bool ReactivatedSinceMarkedDone =>
        !string.IsNullOrEmpty(MarkedDoneHeadSha)
        && !string.IsNullOrEmpty(CurrentHeadSha)
        && !string.Equals(MarkedDoneHeadSha, CurrentHeadSha, StringComparison.Ordinal);

    public static InboxRow FromRow(
        PullRequestRow row,
        int openThreads,
        int unresolvedBot,
        DriftInfo? drift = null,
        int likelyDone = 0)
    {
        drift ??= DriftInfo.Unknown;
        return new(
            row.Url,
            row.DisplayRepo,
            row.Number,
            row.Title,
            row.AuthorLogin,
            row.SourceId,
            row.SourceKind,
            row.IdentityUsed,
            row.Status,
            row.EnrichState,
            row.LastSyncedAt,
            openThreads,
            unresolvedBot,
            drift.Kind,
            drift.CommitsAhead,
            drift.LastReviewedHeadSha,
            drift.CurrentHeadSha,
            row.IsIgnored,
            row.DisappearedAt,
            likelyDone,
            row.LastUpstreamUpdatedAt,
            row.MarkedDoneHeadSha,
            row.MarkedDoneAt);
    }
}

/// <summary>
/// Singleton state container for the inbox. Components subscribe to
/// <see cref="Changed"/> to know when to re-render. Updates are
/// thread-safe (lock around the dictionary). The hosted sync service
/// is the only writer; components are read-only consumers.
/// </summary>
public sealed class InboxState
{
    private readonly object _lock = new();
    private readonly Dictionary<string, InboxRow> _rows = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _lastSyncUtc;
    private string? _lastSyncMessage;

    /// <summary>Raised whenever rows change or a sync milestone occurs.</summary>
    public event Action? Changed;

    public IReadOnlyList<InboxRow> CurrentRows
    {
        get
        {
            lock (_lock)
            {
                return _rows.Values
                    .OrderByDescending(r => r.LastSyncedAt)
                    .ToArray();
            }
        }
    }

    public DateTimeOffset? LastSyncUtc
    {
        get { lock (_lock) return _lastSyncUtc; }
    }

    public string? LastSyncMessage
    {
        get { lock (_lock) return _lastSyncMessage; }
    }

    public void ReplaceAll(IEnumerable<InboxRow> rows)
    {
        lock (_lock)
        {
            _rows.Clear();
            foreach (var r in rows) _rows[r.Url] = r;
        }
        RaiseChanged();
    }

    public void Upsert(InboxRow row)
    {
        lock (_lock)
        {
            _rows[row.Url] = row;
        }
        RaiseChanged();
    }

    public void NoteSync(string message)
    {
        lock (_lock)
        {
            _lastSyncUtc = DateTimeOffset.UtcNow;
            _lastSyncMessage = message;
        }
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch
        {
            // Subscribers run on the render dispatcher; swallow exceptions
            // here so a faulty subscriber can't break the sync loop.
        }
    }
}
