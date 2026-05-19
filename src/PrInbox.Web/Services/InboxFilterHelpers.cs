namespace PrInbox.Web.Services;

/// <summary>
/// Sort order for the author / repo filter popovers on the inbox.
/// Persisted per-popover so users can have e.g. authors-by-recent and
/// repos-by-count simultaneously.
/// </summary>
public enum InboxFilterSortMode
{
    /// <summary>Highest PR count first, then alphabetic name.</summary>
    Count,

    /// <summary>
    /// Most recently active first (by <see cref="IInboxFilterItem.MaxRecent"/>),
    /// non-null values before null, then alphabetic name as tie-breaker.
    /// </summary>
    Recent,
}

/// <summary>
/// Shared shape for filter-popover items so the sort and tally helpers can
/// operate uniformly on both <c>RepoFilterItem</c> and <c>AuthorFilterItem</c>.
/// </summary>
public interface IInboxFilterItem
{
    /// <summary>Unique key (repo name / author login / unknown-author key).</summary>
    string Name { get; }

    /// <summary>PR count after the current filter pipeline.</summary>
    int Count { get; }

    /// <summary>True when the user has excluded this group.</summary>
    bool Excluded { get; }

    /// <summary>
    /// Greatest <c>LastUpstreamUpdatedAt</c> observed across this group's
    /// rows, or <c>null</c> if no row in the group has a value yet.
    /// </summary>
    DateTimeOffset? MaxRecent { get; }
}

/// <summary>
/// Sort + tally helpers for the inbox filter popovers. Lives outside
/// <c>Inbox.razor</c> so it can be unit-tested directly.
/// </summary>
public static class InboxFilterHelpers
{
    /// <summary>
    /// Sort a sequence of filter items by the user's chosen mode. Count mode
    /// preserves the legacy ordering (count desc, name asc); Recent mode
    /// surfaces actors with the freshest upstream activity, demoting
    /// groups whose <see cref="IInboxFilterItem.MaxRecent"/> is null to
    /// the bottom of the list.
    /// </summary>
    public static IEnumerable<T> Sort<T>(IEnumerable<T> items, InboxFilterSortMode mode)
        where T : IInboxFilterItem
    {
        if (mode == InboxFilterSortMode.Recent)
        {
            return items
                .OrderByDescending(i => i.MaxRecent.HasValue)
                .ThenByDescending(i => i.MaxRecent ?? DateTimeOffset.MinValue)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase);
        }
        return items
            .OrderByDescending(i => i.Count)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pill-label tally for filter popovers. Only counts groups with
    /// <c>Count &gt; 0</c>, so stale exclusions (groups in the denylist
    /// that no longer match any row) don't inflate the "hidden" tally.
    /// </summary>
    /// <returns>
    /// <c>(Visible, Hidden)</c> — counts of groups currently shown vs hidden
    /// in the actual table.
    /// </returns>
    public static (int Visible, int Hidden) CountsForPill<T>(IEnumerable<T> items)
        where T : IInboxFilterItem
    {
        var visible = 0;
        var hidden = 0;
        foreach (var i in items)
        {
            if (i.Count == 0) continue;
            if (i.Excluded) hidden++;
            else visible++;
        }
        return (visible, hidden);
    }
}
