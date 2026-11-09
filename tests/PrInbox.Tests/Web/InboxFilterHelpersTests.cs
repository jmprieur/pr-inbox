using FluentAssertions;
using PrInbox.Core.Models;
using PrInbox.Core.Storage;
using PrInbox.Web.Services;

namespace PrInbox.Tests.Web;

/// <summary>
/// Tests for <see cref="InboxFilterHelpers"/>. These helpers feed both
/// filter popovers (repos + authors) on the inbox; correctness of sort
/// + pill tally is verified once here so both call sites stay aligned.
/// </summary>
public class InboxFilterHelpersTests
{
    [Theory]
    [InlineData("alice@example.com", "Alice Smith", "Alice Smith (alice@example.com)")]
    [InlineData("alice@example.com", " Alice Smith ", "Alice Smith (alice@example.com)")]
    [InlineData("alice@example.com", null, "alice@example.com")]
    [InlineData("alice@example.com", " ", "alice@example.com")]
    [InlineData("alice@example.com", "ALICE@example.com", "alice@example.com")]
    [InlineData("octocat", null, "octocat")]
    [InlineData("(unknown)", null, "(unknown)")]
    [InlineData("(unknown)", "Alice Smith", "(unknown)")]
    public void AuthorDisplayLabel_Uses_Name_And_Login_Without_An_Added_At_Prefix(
        string key, string? displayName, string expected)
        => InboxFilterHelpers.AuthorDisplayLabel(key, displayName).Should().Be(expected);

    [Theory]
    [InlineData("alice@example.com", "Alice Smith", " SMITH ", true)]
    [InlineData("alice@example.com", "Alice Smith", "ALICE@EXAMPLE", true)]
    [InlineData("alice@example.com", "Alice Smith", "bob", false)]
    [InlineData("alice@example.com", null, "example.com", true)]
    [InlineData("octocat", null, "OCTO", true)]
    [InlineData("(unknown)", null, "unknown", true)]
    [InlineData("alice@example.com", "Alice Smith", " ", true)]
    public void AuthorMatchesSearch_Matches_Name_Or_Login_Case_Insensitively(
        string key, string? displayName, string search, bool expected)
    {
        var label = InboxFilterHelpers.AuthorDisplayLabel(key, displayName);
        InboxFilterHelpers.AuthorMatchesSearch(key, label, search).Should().Be(expected);
    }

    [Fact]
    public void InboxRow_FromRow_Carries_AuthorDisplayName_And_Preserves_Filter_Key()
    {
        var row = new PullRequestRow(
            new PrIdentity("https://dev.azure.com/fabrikam/Context/_git/MyRepo/pullrequest/1", "ado:1#1"),
            SourceKind.AzureDevOps, "ado:fabrikam/Context", "Context/MyRepo", 1, "Test",
            "alice@example.com", "https://dev.azure.com/fabrikam/Context/_git/MyRepo/pullrequest/1",
            PullRequestStatus.Open, TrackingReason.Assigned, "self",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, EnrichState.Basic,
            null, null, null, AuthorDisplayName: "Alice Smith");

        var inboxRow = InboxRow.FromRow(row, openThreads: 0, unresolvedBot: 0);

        inboxRow.AuthorDisplayName.Should().Be("Alice Smith");
        InboxFilters.AuthorKeyOf(inboxRow.AuthorLogin).Should().Be("alice@example.com");
        var filters = InboxFilters.From(
            showClosed: false, showIgnored: false,
            enabledSources: InboxFilters.KnownSourceClasses,
            excludedRepos: Array.Empty<string>(),
            excludedAuthors: new[] { "ALICE@example.com" },
            ignoredRepoRegexes: Array.Empty<System.Text.RegularExpressions.Regex>());
        filters.ShouldShow(inboxRow).Should().BeFalse();
    }

    private sealed record TestItem(
        string Name,
        int Count,
        bool Excluded,
        DateTimeOffset? MaxRecent) : IInboxFilterItem;

    private sealed record NamedItem(string Name, string DisplayLabel, int Count) : IInboxFilterItem
    {
        public bool Excluded => false;
        public DateTimeOffset? MaxRecent => null;
    }

    [Fact]
    public void Sort_Alphabetical_Uses_Display_Label_Not_Login_Count_Or_Activity()
    {
        var items = new[]
        {
            new NamedItem("a@example.com", "Zoe (a@example.com)", 50),
            new NamedItem("z@example.com", "alice (z@example.com)", 1),
            new NamedItem("bob", "Bob", 0),
        };

        InboxFilterHelpers.Sort(items, InboxFilterSortMode.Alphabetical)
            .Select(i => i.Name).Should().Equal("z@example.com", "bob", "a@example.com");
    }

    [Fact]
    public void Sort_Alphabetical_Defaults_To_Key_When_No_Display_Label_Is_Provided()
    {
        var items = new[]
        {
            new TestItem("zeta", 9, false, DateTimeOffset.UtcNow),
            new TestItem("Alpha", 1, false, null),
        };

        InboxFilterHelpers.Sort(items, InboxFilterSortMode.Alphabetical)
            .Select(i => i.Name).Should().Equal("Alpha", "zeta");
    }

    [Theory]
    [InlineData(InboxFilterSortMode.Alphabetical)]
    [InlineData(InboxFilterSortMode.Count)]
    [InlineData(InboxFilterSortMode.Recent)]
    public void Sort_Combines_Author_Sources_By_Display_Name_For_Alphabetic_Order_And_Ties(
        InboxFilterSortMode mode)
    {
        var items = new[]
        {
            new NamedItem("aaron@example.com",
                InboxFilterHelpers.AuthorDisplayLabel("aaron@example.com", "Zoe"), 0),
            new NamedItem("bravo", InboxFilterHelpers.AuthorDisplayLabel("bravo", null), 0),
            new NamedItem("zulu@example.com",
                InboxFilterHelpers.AuthorDisplayLabel("zulu@example.com", "Alice"), 0),
            new NamedItem("delta", InboxFilterHelpers.AuthorDisplayLabel("delta", null), 0),
            new NamedItem("a@example.com",
                InboxFilterHelpers.AuthorDisplayLabel("a@example.com", "Carlos"), 0),
        };

        InboxFilterHelpers.Sort(items, mode).Select(i => i.Name)
            .Should().Equal("zulu@example.com", "bravo", "a@example.com", "delta", "aaron@example.com");
    }

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
