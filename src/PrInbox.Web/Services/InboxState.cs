using PrInbox.Core.Models;
using PrInbox.Core.Storage;

namespace PrInbox.Web.Services;

/// <summary>
/// Classification of a PR's changed-file data for monorepo path scoping.
/// Drives a fail-open filter: only <see cref="Complete"/> rows can ever be
/// hidden for being out-of-scope — every other state shows.
/// </summary>
public enum TouchedPathState
{
    /// <summary>Not enriched yet; changed files unknown. Show (pending).</summary>
    Unknown,

    /// <summary>Complete changed-file list available. Eligible for matching.</summary>
    Complete,

    /// <summary>Enriched but no usable file list (ADO, a failed files fetch,
    /// or a genuinely zero-file PR). Show — we can't classify scope.</summary>
    Unavailable,

    /// <summary>File list too large / capped (e.g. GitHub's 3000-file cap). Show.</summary>
    Truncated,
}

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
    DateTimeOffset? MarkedDoneAt = null,
    DateTimeOffset? FlaggedAt = null,
    DateTimeOffset? UpstreamCreatedAt = null,
    IReadOnlyList<TagRow>? Tags = null,
    IReadOnlyList<string>? TouchedPaths = null,
    TouchedPathState TouchedPathState = TouchedPathState.Unknown,
    MyRole MyRole = MyRole.Reviewer)
{
    /// <summary>
    /// True when the user has flagged this PR as "of interest." Flag is
    /// orthogonal to done / ignore / closed — flagging does not bypass
    /// any other filter. The "Show only flagged" toolbar toggle isolates
    /// the dashboard to just these rows.
    /// </summary>
    public bool IsFlagged => FlaggedAt.HasValue;

    /// <summary>
    /// Tags attached to this PR. Never null at the call site — components
    /// can iterate safely. Use <see cref="HasTags"/> to test for presence.
    /// </summary>
    public IReadOnlyList<TagRow> TagsSafe => Tags ?? Array.Empty<TagRow>();

    public bool HasTags => Tags is not null && Tags.Count > 0;

    /// <summary>Changed paths, never null for safe iteration.</summary>
    public IReadOnlyList<string> TouchedPathsSafe => TouchedPaths ?? Array.Empty<string>();

    /// <summary>
    /// GitHub's PR "list files" endpoint returns at most this many entries;
    /// a PR with more changed files comes back truncated. We treat a file
    /// list at (or above) the cap as <see cref="TouchedPathState.Truncated"/>
    /// so path scoping fails open rather than hiding a huge PR on a partial
    /// view of its files.
    /// </summary>
    public const int GitHubChangedFileCap = 3000;

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
        int likelyDone = 0,
        IReadOnlyList<TagRow>? tags = null,
        IReadOnlyList<SnapshotFileChange>? snapshotFiles = null)
    {
        drift ??= DriftInfo.Unknown;
        var (touchedPaths, touchedState) = ClassifyTouchedPaths(row.EnrichState, snapshotFiles);
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
            row.MarkedDoneAt,
            row.FlaggedAt,
            row.UpstreamCreatedAt,
            tags,
            touchedPaths,
            touchedState,
            row.MyRole);
    }

    /// <summary>
    /// Derives the changed-path signal used by monorepo path scoping from
    /// the row's enrichment state and its latest snapshot's file list.
    /// Fail-open by construction: only <see cref="TouchedPathState.Complete"/>
    /// yields a real path list eligible to hide a row; every other state is
    /// a "show anyway" signal.
    /// </summary>
    internal static (IReadOnlyList<string>? Paths, TouchedPathState State) ClassifyTouchedPaths(
        EnrichState enrichState,
        IReadOnlyList<SnapshotFileChange>? files)
    {
        // Not enriched yet -> changed files are simply unknown. Show (pending).
        if (enrichState != EnrichState.Enriched)
            return (null, TouchedPathState.Unknown);

        // Enriched but no file list -> ADO (never populates Files) or a failed
        // GitHub files fetch. We can't classify; show.
        if (files is null)
            return (null, TouchedPathState.Unavailable);

        // At/over the platform cap -> the list may be partial; fail open.
        if (files.Count >= GitHubChangedFileCap)
            return (null, TouchedPathState.Truncated);

        var paths = new List<string>(files.Count);
        foreach (var f in files)
        {
            if (!string.IsNullOrWhiteSpace(f.Path)) paths.Add(f.Path);
        }

        // No usable path -> can't decide scope (a genuinely zero-file PR, or
        // a degenerate/empty fetch result). Fail open rather than hide on an
        // empty signal. This also keeps us consistent with the persistence
        // layer, which stores an empty file list as NULL (-> Unavailable).
        if (paths.Count == 0)
            return (null, TouchedPathState.Unavailable);

        return (paths, TouchedPathState.Complete);
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
