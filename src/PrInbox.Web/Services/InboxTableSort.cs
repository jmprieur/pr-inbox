namespace PrInbox.Web.Services;

public enum InboxTableSortColumn
{
    Age,
    Title,
    Repo,
    Author,
}

/// <summary>One active table sort; defaults to the oldest PRs first.</summary>
public sealed record InboxTableSort(
    InboxTableSortColumn Column = InboxTableSortColumn.Age,
    bool Descending = false)
{
    public static InboxTableSort FromPreferences(string? sort, string? legacyAge)
    {
        var parts = sort?.Split(':');
        if (parts is { Length: 2 }
            && Enum.TryParse<InboxTableSortColumn>(parts[0], ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed)
            && bool.TryParse(parts[1], out var descending))
        {
            return new(parsed, descending);
        }

        return new(Descending: string.Equals(legacyAge, nameof(AgeSortMode.NewestFirst),
            StringComparison.OrdinalIgnoreCase));
    }

    public string ToPreference() => $"{Column}:{Descending}";

    public InboxTableSort Toggle(InboxTableSortColumn column) =>
        new(column, Column == column && !Descending);

    public string AriaSort(InboxTableSortColumn column) =>
        Column != column ? "none" : Descending ? "descending" : "ascending";

    public IReadOnlyList<InboxRow> Order(IReadOnlyList<InboxRow> rows)
    {
        if (Column == InboxTableSortColumn.Age)
            return InboxAgeSort.Order(rows, Descending ? AgeSortMode.NewestFirst : AgeSortMode.OldestFirst);

        // Keep the default age order as a stable tie-breaker for text columns.
        var byAge = InboxAgeSort.Order(rows, AgeSortMode.OldestFirst);
        Func<InboxRow, string?> key = Column switch
        {
            InboxTableSortColumn.Title => r => r.Title,
            InboxTableSortColumn.Repo => r => r.DisplayRepo,
            InboxTableSortColumn.Author => r => r.AuthorLabel,
            _ => throw new ArgumentOutOfRangeException(nameof(Column), Column, "Unknown table sort column."),
        };
        var withKnownValuesFirst = byAge.OrderByDescending(r => !string.IsNullOrWhiteSpace(key(r)));
        var ordered = Descending
            ? withKnownValuesFirst.ThenByDescending(key, StringComparer.OrdinalIgnoreCase)
            : withKnownValuesFirst.ThenBy(key, StringComparer.OrdinalIgnoreCase);
        if (Column == InboxTableSortColumn.Repo)
            ordered = Descending ? ordered.ThenByDescending(r => r.Number) : ordered.ThenBy(r => r.Number);
        return ordered.ToList();
    }
}
