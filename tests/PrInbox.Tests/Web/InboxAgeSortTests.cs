using FluentAssertions;
using PrInbox.Core.Models;
using PrInbox.Core.Storage;
using PrInbox.Web.Services;

namespace PrInbox.Tests.Web;

/// <summary>
/// Tests for <see cref="InboxAgeSort"/>, which orders the inbox table by the
/// upstream "opened" timestamp behind the sortable Age column. Rows with an
/// unknown opened date sort last in both directions, and ordering is stable.
/// </summary>
public class InboxAgeSortTests
{
    private static InboxRow Row(string url, DateTimeOffset? createdAt)
        => new(
            Url: url,
            DisplayRepo: "o/r",
            Number: 1,
            Title: url,
            AuthorLogin: "octocat",
            SourceId: "gh.com",
            SourceKind: SourceKind.GitHub,
            IdentityUsed: "id",
            Status: PullRequestStatus.Open,
            EnrichState: EnrichState.Basic,
            LastSyncedAt: DateTimeOffset.Parse("2026-05-13T20:30:00Z"),
            OpenThreadCount: 0,
            UnresolvedBotCount: 0,
            DriftKind: DriftKind.Unknown,
            DriftCount: 0,
            LastReviewedHeadSha: null,
            CurrentHeadSha: null,
            UpstreamCreatedAt: createdAt);

    private static readonly DateTimeOffset Old   = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static readonly DateTimeOffset Mid   = DateTimeOffset.Parse("2026-03-01T00:00:00Z");
    private static readonly DateTimeOffset Young = DateTimeOffset.Parse("2026-05-01T00:00:00Z");

    [Fact]
    public void None_Preserves_Incoming_Order()
    {
        var rows = new[] { Row("c", Young), Row("a", Old), Row("b", Mid) };

        var ordered = InboxAgeSort.Order(rows, AgeSortMode.None);

        ordered.Select(r => r.Url).Should().Equal("c", "a", "b");
    }

    [Fact]
    public void OldestFirst_Orders_By_CreatedAt_Ascending()
    {
        var rows = new[] { Row("young", Young), Row("old", Old), Row("mid", Mid) };

        var ordered = InboxAgeSort.Order(rows, AgeSortMode.OldestFirst);

        ordered.Select(r => r.Url).Should().Equal("old", "mid", "young");
    }

    [Fact]
    public void NewestFirst_Orders_By_CreatedAt_Descending()
    {
        var rows = new[] { Row("young", Young), Row("old", Old), Row("mid", Mid) };

        var ordered = InboxAgeSort.Order(rows, AgeSortMode.NewestFirst);

        ordered.Select(r => r.Url).Should().Equal("young", "mid", "old");
    }

    [Fact]
    public void OldestFirst_Sorts_Nulls_Last()
    {
        var rows = new[] { Row("n1", null), Row("young", Young), Row("old", Old) };

        var ordered = InboxAgeSort.Order(rows, AgeSortMode.OldestFirst);

        ordered.Select(r => r.Url).Should().Equal("old", "young", "n1");
    }

    [Fact]
    public void NewestFirst_Sorts_Nulls_Last()
    {
        var rows = new[] { Row("n1", null), Row("young", Young), Row("old", Old) };

        var ordered = InboxAgeSort.Order(rows, AgeSortMode.NewestFirst);

        ordered.Select(r => r.Url).Should().Equal("young", "old", "n1");
    }

    [Fact]
    public void Nulls_Preserve_Relative_Order_Stable()
    {
        var rows = new[] { Row("n1", null), Row("old", Old), Row("n2", null) };

        var ordered = InboxAgeSort.Order(rows, AgeSortMode.OldestFirst);

        // Both nulls land after the dated row, in their original relative order.
        ordered.Select(r => r.Url).Should().Equal("old", "n1", "n2");
    }
}
