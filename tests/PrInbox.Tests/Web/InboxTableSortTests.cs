using PrInbox.Core.Models;
using PrInbox.Web.Services;

namespace PrInbox.Tests.Web;

public class InboxTableSortTests
{
    private static InboxRow Row(string url, int ageOrder) => new(
        Url: url, DisplayRepo: "repo", Number: 1, Title: url, AuthorLogin: "author",
        SourceId: "gh.com", SourceKind: SourceKind.GitHub, IdentityUsed: "self",
        Status: PullRequestStatus.Open, EnrichState: EnrichState.Basic,
        LastSyncedAt: DateTimeOffset.UnixEpoch, OpenThreadCount: 0, UnresolvedBotCount: 0,
        DriftKind: DriftKind.Unknown, DriftCount: 0, LastReviewedHeadSha: null, CurrentHeadSha: null,
        UpstreamCreatedAt: DateTimeOffset.UnixEpoch.AddDays(ageOrder));

    [Fact]
    public void Author_Labels_Use_The_Same_Format_For_Ado_And_GitHub()
    {
        var ado = Row("ado", 1) with
        {
            SourceKind = SourceKind.AzureDevOps,
            AuthorLogin = "alice@example.com",
            AuthorDisplayName = "Alice Smith",
        };
        var github = Row("github", 2) with
        {
            AuthorLogin = "octocat",
            AuthorDisplayName = "Alice Smith",
        };

        ado.AuthorLabel.Should().Be("Alice Smith (alice@example.com)");
        github.AuthorLabel.Should().Be("Alice Smith (octocat)");
    }

    [Theory]
    [InlineData("octocat", " Alice Smith ", "Alice Smith (octocat)")]
    [InlineData("octocat", null, "octocat")]
    [InlineData("octocat", " ", "octocat")]
    [InlineData("octocat", "OCTOCAT", "octocat")]
    [InlineData(null, "Alice Smith", "Alice Smith")]
    [InlineData("", " Alice Smith ", "Alice Smith")]
    [InlineData(null, null, "")]
    [InlineData(" ", " ", "")]
    public void Author_Label_Handles_Missing_And_Duplicate_Names(
        string? login, string? name, string expected)
    {
        var row = Row("pr", 1) with { AuthorLogin = login, AuthorDisplayName = name };

        row.AuthorLabel.Should().Be(expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Author_Sorts_By_Displayed_Name_Across_Sources_With_Login_Fallback(bool descending)
    {
        var rows = new[]
        {
            Row("zoe", 1) with
            {
                SourceKind = SourceKind.AzureDevOps,
                AuthorLogin = "aaron@example.com",
                AuthorDisplayName = " Zoe Smith ",
            },
            Row("bravo", 2) with { AuthorLogin = "bravo", AuthorDisplayName = " " },
            Row("alice", 3) with { AuthorLogin = "zulu", AuthorDisplayName = "alice Smith" },
            Row("unknown", 4) with { AuthorLogin = null },
            Row("name-only", 5) with { AuthorLogin = null, AuthorDisplayName = "Carlos Smith" },
        };

        new InboxTableSort(InboxTableSortColumn.Author, descending).Order(rows)
            .Select(r => r.Url).Should().Equal(descending
                ? new[] { "zoe", "name-only", "bravo", "alice", "unknown" }
                : new[] { "alice", "bravo", "name-only", "zoe", "unknown" });
    }

    [Fact]
    public void Default_Sorts_By_Age_Oldest_First_With_Unknown_Last()
    {
        var rows = new[]
        {
            Row("unknown", 0) with { UpstreamCreatedAt = null },
            Row("young", 3), Row("old", 1), Row("middle", 2),
        };

        new InboxTableSort().Order(rows).Select(r => r.Url)
            .Should().Equal("old", "middle", "young", "unknown");
    }

    [Theory]
    [InlineData(InboxTableSortColumn.Title, false)]
    [InlineData(InboxTableSortColumn.Title, true)]
    [InlineData(InboxTableSortColumn.Repo, false)]
    [InlineData(InboxTableSortColumn.Repo, true)]
    [InlineData(InboxTableSortColumn.Author, false)]
    [InlineData(InboxTableSortColumn.Author, true)]
    public void Text_Columns_Sort_Case_Insensitively_In_Both_Directions(
        InboxTableSortColumn column, bool descending)
    {
        var rows = new[]
        {
            Row("zulu", 1) with { Title = "Zulu", DisplayRepo = "Zulu", AuthorLogin = "Zulu" },
            Row("alpha", 2) with { Title = "alpha", DisplayRepo = "alpha", AuthorLogin = "alpha" },
            Row("bravo", 3) with { Title = "Bravo", DisplayRepo = "Bravo", AuthorLogin = "Bravo" },
        };

        new InboxTableSort(column, descending).Order(rows).Select(r => r.Url)
            .Should().Equal(descending ? new[] { "zulu", "bravo", "alpha" } : new[] { "alpha", "bravo", "zulu" });
    }

    [Theory]
    [InlineData(InboxTableSortColumn.Title, false)]
    [InlineData(InboxTableSortColumn.Title, true)]
    [InlineData(InboxTableSortColumn.Author, false)]
    [InlineData(InboxTableSortColumn.Author, true)]
    public void Missing_Text_Values_Sort_Last(InboxTableSortColumn column, bool descending)
    {
        var rows = new[]
        {
            Row("unknown", 1) with { Title = null, AuthorLogin = null },
            Row("known", 2),
            Row("blank", 3) with { Title = "", AuthorLogin = "" },
        };

        var ordered = new InboxTableSort(column, descending).Order(rows);

        ordered[0].Url.Should().Be("known");
        ordered.Skip(1).Select(r => r.Url).Should().BeEquivalentTo("unknown", "blank");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Text_Ties_Keep_Default_Age_Order(bool descending)
    {
        var rows = new[]
        {
            Row("young", 3) with { Title = "Same" },
            Row("old", 1) with { Title = "same" },
        };

        new InboxTableSort(InboxTableSortColumn.Title, descending).Order(rows)
            .Select(r => r.Url).Should().Equal("old", "young");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Repo_Ties_Sort_PR_Numbers_Numerically(bool descending)
    {
        var rows = new[] { Row("ten", 1) with { Number = 10 }, Row("two", 2) with { Number = 2 } };

        new InboxTableSort(InboxTableSortColumn.Repo, descending).Order(rows)
            .Select(r => r.Url).Should().Equal(descending ? new[] { "ten", "two" } : new[] { "two", "ten" });
    }

    [Theory]
    [InlineData(InboxTableSortColumn.Age)]
    [InlineData(InboxTableSortColumn.Title)]
    [InlineData(InboxTableSortColumn.Repo)]
    [InlineData(InboxTableSortColumn.Author)]
    public void Clicking_An_Active_Header_Reverses_Direction(InboxTableSortColumn column)
    {
        var ascending = new InboxTableSort(column);
        var descending = ascending.Toggle(column);

        descending.Should().Be(new InboxTableSort(column, Descending: true));
        descending.Toggle(column).Should().Be(ascending);
        descending.AriaSort(column).Should().Be("descending");
        ascending.AriaSort(column).Should().Be("ascending");
    }

    [Fact]
    public void Switching_Columns_Starts_Ascending_And_Age_Returns_To_Default()
    {
        var titleSort = new InboxTableSort(InboxTableSortColumn.Repo, Descending: true)
            .Toggle(InboxTableSortColumn.Title);

        titleSort.Should().Be(new InboxTableSort(InboxTableSortColumn.Title));
        titleSort.AriaSort(InboxTableSortColumn.Repo).Should().Be("none");
        titleSort.Toggle(InboxTableSortColumn.Age).Should().Be(new InboxTableSort());
    }

    [Theory]
    [InlineData(InboxTableSortColumn.Age, false)]
    [InlineData(InboxTableSortColumn.Age, true)]
    [InlineData(InboxTableSortColumn.Title, false)]
    [InlineData(InboxTableSortColumn.Title, true)]
    [InlineData(InboxTableSortColumn.Repo, false)]
    [InlineData(InboxTableSortColumn.Repo, true)]
    [InlineData(InboxTableSortColumn.Author, false)]
    [InlineData(InboxTableSortColumn.Author, true)]
    public void Sort_Preference_RoundTrips(InboxTableSortColumn column, bool descending)
    {
        var sort = new InboxTableSort(column, descending);

        InboxTableSort.FromPreferences(sort.ToPreference(), legacyAge: null).Should().Be(sort);
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(null, "None", false)]
    [InlineData(null, "OldestFirst", false)]
    [InlineData(null, "NewestFirst", true)]
    [InlineData("invalid", null, false)]
    [InlineData("999:True", null, false)]
    [InlineData("Title:invalid", null, false)]
    public void Missing_Or_Invalid_Preference_Uses_Age_And_Honors_Legacy_Direction(
        string? preference, string? legacyAge, bool descending)
        => InboxTableSort.FromPreferences(preference, legacyAge)
            .Should().Be(new InboxTableSort(Descending: descending));
}
