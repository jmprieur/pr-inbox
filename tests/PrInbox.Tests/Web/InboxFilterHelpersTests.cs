using FluentAssertions;
using PrInbox.Web.Services;

namespace PrInbox.Tests.Web;

/// <summary>
/// Tests for <see cref="InboxFilterHelpers"/>. These helpers feed both
/// filter popovers (repos + authors) on the inbox; correctness of sort
/// + pill tally is verified once here so both call sites stay aligned.
/// </summary>
public class InboxFilterHelpersTests
{
    private sealed record TestItem(
        string Name,
        int Count,
        bool Excluded,
        DateTimeOffset? MaxRecent) : IInboxFilterItem;

    // ---------- Sort: Count mode ----------

    [Fact]
    public void Sort_Count_Mode_Orders_By_Count_Desc_Then_Name_Asc()
    {
        var items = new[]
        {
            new TestItem("zeta",  3, Excluded: false, MaxRecent: null),
            new TestItem("alpha", 7, Excluded: false, MaxRecent: null),
            new TestItem("mu",    7, Excluded: false, MaxRecent: null),
            new TestItem("beta",  1, Excluded: false, MaxRecent: null),
        };

        var ordered = InboxFilterHelpers.Sort(items, InboxFilterSortMode.Count).ToList();

        ordered.Select(i => i.Name).Should().Equal("alpha", "mu", "zeta", "beta");
    }

    [Fact]
    public void Sort_Count_Mode_Ignores_MaxRecent()
    {
        // Recent timestamps must not change Count-mode ordering.
        var older = DateTimeOffset.Parse("2026-05-10T00:00:00Z");
        var newer = DateTimeOffset.Parse("2026-05-18T00:00:00Z");
        var items = new[]
        {
            new TestItem("alpha", 5, Excluded: false, MaxRecent: older),
            new TestItem("beta",  9, Excluded: false, MaxRecent: newer),
        };

        var ordered = InboxFilterHelpers.Sort(items, InboxFilterSortMode.Count).ToList();

        ordered.Select(i => i.Name).Should().Equal("beta", "alpha");
    }

    // ---------- Sort: Recent mode ----------

    [Fact]
    public void Sort_Recent_Mode_Puts_Null_MaxRecent_Last()
    {
        var t = DateTimeOffset.Parse("2026-05-18T10:00:00Z");
        var items = new[]
        {
            new TestItem("alpha", 1, Excluded: false, MaxRecent: null),
            new TestItem("beta",  1, Excluded: false, MaxRecent: t),
            new TestItem("gamma", 1, Excluded: false, MaxRecent: null),
        };

        var ordered = InboxFilterHelpers.Sort(items, InboxFilterSortMode.Recent).ToList();

        ordered.Select(i => i.Name).Should().Equal("beta", "alpha", "gamma");
    }

    [Fact]
    public void Sort_Recent_Mode_Newer_Before_Older()
    {
        var older = DateTimeOffset.Parse("2026-05-10T00:00:00Z");
        var newer = DateTimeOffset.Parse("2026-05-18T00:00:00Z");
        var items = new[]
        {
            new TestItem("alpha", 1, Excluded: false, MaxRecent: older),
            new TestItem("beta",  1, Excluded: false, MaxRecent: newer),
        };

        var ordered = InboxFilterHelpers.Sort(items, InboxFilterSortMode.Recent).ToList();

        ordered.Select(i => i.Name).Should().Equal("beta", "alpha");
    }

    [Fact]
    public void Sort_Recent_Mode_Ties_Resolved_Alphabetically()
    {
        var t = DateTimeOffset.Parse("2026-05-18T10:00:00Z");
        var items = new[]
        {
            new TestItem("zeta",  1, Excluded: false, MaxRecent: t),
            new TestItem("alpha", 1, Excluded: false, MaxRecent: t),
            new TestItem("mu",    1, Excluded: false, MaxRecent: t),
        };

        var ordered = InboxFilterHelpers.Sort(items, InboxFilterSortMode.Recent).ToList();

        ordered.Select(i => i.Name).Should().Equal("alpha", "mu", "zeta");
    }

    [Fact]
    public void Sort_Recent_Mode_With_All_Nulls_Falls_Back_To_Alphabetic()
    {
        // Backfill window: every group's max is null until fast-sync runs.
        // Must produce a stable, alphabetic order rather than insertion order.
        var items = new[]
        {
            new TestItem("zeta",  3, Excluded: false, MaxRecent: null),
            new TestItem("alpha", 5, Excluded: false, MaxRecent: null),
            new TestItem("mu",    1, Excluded: false, MaxRecent: null),
        };

        var ordered = InboxFilterHelpers.Sort(items, InboxFilterSortMode.Recent).ToList();

        ordered.Select(i => i.Name).Should().Equal("alpha", "mu", "zeta");
    }

    // ---------- CountsForPill ----------

    [Fact]
    public void CountsForPill_Splits_Visible_And_Hidden()
    {
        var items = new[]
        {
            new TestItem("a", 3, Excluded: false, MaxRecent: null),
            new TestItem("b", 2, Excluded: true,  MaxRecent: null),
            new TestItem("c", 4, Excluded: false, MaxRecent: null),
            new TestItem("d", 1, Excluded: true,  MaxRecent: null),
        };

        var (visible, hidden) = InboxFilterHelpers.CountsForPill(items);

        visible.Should().Be(2);
        hidden.Should().Be(2);
    }

    [Fact]
    public void CountsForPill_Ignores_Zero_Count_Groups()
    {
        // The rubber-duck blocker: stale exclusions (still in the denylist
        // but no longer matching the current filter universe) appear with
        // Count == 0 and must not inflate the "hidden" tally.
        var items = new[]
        {
            new TestItem("active",        3, Excluded: false, MaxRecent: null),
            new TestItem("active-hidden", 2, Excluded: true,  MaxRecent: null),
            new TestItem("stale-hidden",  0, Excluded: true,  MaxRecent: null), // ignored
            new TestItem("stale-other",   0, Excluded: false, MaxRecent: null), // ignored
        };

        var (visible, hidden) = InboxFilterHelpers.CountsForPill(items);

        visible.Should().Be(1);
        hidden.Should().Be(1);
    }

    [Fact]
    public void CountsForPill_Empty_Returns_Zero_Zero()
    {
        var (visible, hidden) = InboxFilterHelpers.CountsForPill(Array.Empty<TestItem>());

        visible.Should().Be(0);
        hidden.Should().Be(0);
    }

    [Fact]
    public void CountsForPill_All_Visible_Returns_Zero_Hidden()
    {
        var items = new[]
        {
            new TestItem("a", 3, Excluded: false, MaxRecent: null),
            new TestItem("b", 1, Excluded: false, MaxRecent: null),
        };

        var (visible, hidden) = InboxFilterHelpers.CountsForPill(items);

        visible.Should().Be(2);
        hidden.Should().Be(0);
    }
}
