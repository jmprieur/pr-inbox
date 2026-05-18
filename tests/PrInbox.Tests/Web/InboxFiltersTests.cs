using System.Text.RegularExpressions;
using FluentAssertions;
using PrInbox.Core.Models;
using PrInbox.Core.Storage;
using PrInbox.Web.Services;

namespace PrInbox.Tests.Web;

/// <summary>
/// Tests for <see cref="InboxFilters"/>. Parity with the legacy
/// <c>Inbox.razor.VisibleRows</c> pipeline is critical — the dashboard
/// and the background sync prioritizer both rely on
/// <see cref="InboxFilters.ShouldShow(PullRequestRow)"/> agreeing on which
/// rows the user can see. Each filter step has its own case.
/// </summary>
public class InboxFiltersTests
{
    // ---------- fixture helpers ----------

    private static PullRequestRow MakePr(
        string sourceId = "gh.com:public",
        string displayRepo = "owner/repo",
        string? authorLogin = "octo",
        PullRequestStatus status = PullRequestStatus.Open,
        bool isIgnored = false,
        DateTimeOffset? disappearedAt = null)
        => new(
            Identity: new PrIdentity($"https://github.com/{displayRepo}/pull/1", $"{sourceId}:1#1"),
            SourceKind: SourceKind.GitHub,
            SourceId: sourceId,
            DisplayRepo: displayRepo,
            Number: 1,
            Title: "Test",
            AuthorLogin: authorLogin,
            Url: $"https://github.com/{displayRepo}/pull/1",
            Status: status,
            TrackingReason: TrackingReason.Assigned,
            IdentityUsed: "jmprieur",
            FirstSeenAt: DateTimeOffset.UnixEpoch,
            LastSyncedAt: DateTimeOffset.UnixEpoch,
            EnrichState: EnrichState.Enriched,
            LastBriefedHeadSha: null,
            LastReviewRunHeadSha: null,
            LastPostedReviewHeadSha: null,
            IsIgnored: isIgnored,
            DisappearedAt: disappearedAt);

    /// <summary>Filter with all defaults — nothing excluded, all chips on,
    /// no closed/ignored visible. Matches the "fresh install" UI state.</summary>
    private static InboxFilters DefaultFilters() => InboxFilters.From(
        showClosed: false,
        showIgnored: false,
        enabledSources: InboxFilters.KnownSourceClasses,
        excludedRepos: Array.Empty<string>(),
        excludedAuthors: Array.Empty<string>(),
        ignoredRepoRegexes: Array.Empty<Regex>());

    // ---------- SourceClassOf ----------

    [Theory]
    [InlineData("gh.com:emu", "src-emu")]
    [InlineData("gh.com:public", "src-public")]
    [InlineData("ghe.contoso", "src-ghe")]
    [InlineData("ghe.northwind.com", "src-ghe")]
    [InlineData("ado:mseng", "src-ado")]
    [InlineData("ado:msazure/foo", "src-ado")]
    [InlineData("unknown:thing", "src-other")]
    public void SourceClassOf_maps_known_prefixes(string sourceId, string expected)
        => InboxFilters.SourceClassOf(sourceId).Should().Be(expected);

    // ---------- AuthorKeyOf ----------

    [Theory]
    [InlineData(null, "(unknown)")]
    [InlineData("", "(unknown)")]
    [InlineData("  ", "(unknown)")]
    [InlineData("octo", "octo")]
    [InlineData("rcastiglione", "rcastiglione")]
    public void AuthorKeyOf_buckets_null_and_empty(string? input, string expected)
        => InboxFilters.AuthorKeyOf(input).Should().Be(expected);

    // ---------- Default filters: open rows visible, closed hidden ----------

    [Fact]
    public void Default_filters_show_open_assigned_pr()
        => DefaultFilters().ShouldShow(MakePr()).Should().BeTrue();

    [Theory]
    [InlineData(PullRequestStatus.Closed)]
    [InlineData(PullRequestStatus.Merged)]
    public void Default_filters_hide_closed_or_merged(PullRequestStatus status)
        => DefaultFilters().ShouldShow(MakePr(status: status)).Should().BeFalse();

    [Fact]
    public void ShowClosed_reveals_merged_prs()
    {
        var filters = InboxFilters.From(
            showClosed: true, showIgnored: false,
            enabledSources: InboxFilters.KnownSourceClasses,
            excludedRepos: Array.Empty<string>(),
            excludedAuthors: Array.Empty<string>(),
            ignoredRepoRegexes: Array.Empty<Regex>());
        filters.ShouldShow(MakePr(status: PullRequestStatus.Merged)).Should().BeTrue();
    }

    // ---------- Source chips ----------

    [Fact]
    public void All_chips_on_keeps_unknown_sources_visible()
    {
        // When the chip set is "full", unknown sources (src-other) are
        // intentionally NOT enforced as excluded — opt-out semantics.
        var filters = DefaultFilters();
        filters.ShouldShow(MakePr(sourceId: "unknown:thing")).Should().BeTrue();
    }

    [Fact]
    public void Partial_chip_set_hides_disabled_sources()
    {
        var filters = InboxFilters.From(
            showClosed: false, showIgnored: false,
            enabledSources: new[] { "src-public" },           // only public on
            excludedRepos: Array.Empty<string>(),
            excludedAuthors: Array.Empty<string>(),
            ignoredRepoRegexes: Array.Empty<Regex>());

        filters.ShouldShow(MakePr(sourceId: "gh.com:public")).Should().BeTrue();
        filters.ShouldShow(MakePr(sourceId: "gh.com:emu")).Should().BeFalse();
        filters.ShouldShow(MakePr(sourceId: "ado:mseng")).Should().BeFalse();
    }

    [Fact]
    public void Partial_chip_set_also_hides_unknown_sources()
    {
        // Once the user has actively unchecked any chip, unknown sources
        // are no longer free-passed — matches Razor behavior (a row only
        // passes if its class is in the explicit allow-list).
        var filters = InboxFilters.From(
            showClosed: false, showIgnored: false,
            enabledSources: new[] { "src-public", "src-emu", "src-ghe" }, // ado off
            excludedRepos: Array.Empty<string>(),
            excludedAuthors: Array.Empty<string>(),
            ignoredRepoRegexes: Array.Empty<Regex>());

        filters.ShouldShow(MakePr(sourceId: "unknown:thing")).Should().BeFalse();
    }

    // ---------- Per-repo denylist ----------

    [Fact]
    public void Excluded_repo_is_hidden()
    {
        var filters = InboxFilters.From(
            showClosed: false, showIgnored: false,
            enabledSources: InboxFilters.KnownSourceClasses,
            excludedRepos: new[] { "owner/blocked" },
            excludedAuthors: Array.Empty<string>(),
            ignoredRepoRegexes: Array.Empty<Regex>());

        filters.ShouldShow(MakePr(displayRepo: "owner/blocked")).Should().BeFalse();
        filters.ShouldShow(MakePr(displayRepo: "owner/allowed")).Should().BeTrue();
    }

    [Fact]
    public void Excluded_repo_match_is_case_insensitive()
    {
        var filters = InboxFilters.From(
            showClosed: false, showIgnored: false,
            enabledSources: InboxFilters.KnownSourceClasses,
            excludedRepos: new[] { "Owner/Blocked" },
            excludedAuthors: Array.Empty<string>(),
            ignoredRepoRegexes: Array.Empty<Regex>());

        filters.ShouldShow(MakePr(displayRepo: "owner/blocked")).Should().BeFalse();
    }

    // ---------- Per-author denylist ----------

    [Fact]
    public void Excluded_author_is_hidden()
    {
        var filters = InboxFilters.From(
            showClosed: false, showIgnored: false,
            enabledSources: InboxFilters.KnownSourceClasses,
            excludedRepos: Array.Empty<string>(),
            excludedAuthors: new[] { "noisy-bot" },
            ignoredRepoRegexes: Array.Empty<Regex>());

        filters.ShouldShow(MakePr(authorLogin: "noisy-bot")).Should().BeFalse();
        filters.ShouldShow(MakePr(authorLogin: "octo")).Should().BeTrue();
    }

    [Fact]
    public void Excluded_unknown_author_bucket_hides_null_and_empty_authors()
    {
        var filters = InboxFilters.From(
            showClosed: false, showIgnored: false,
            enabledSources: InboxFilters.KnownSourceClasses,
            excludedRepos: Array.Empty<string>(),
            excludedAuthors: new[] { InboxFilters.UnknownAuthorKey },
            ignoredRepoRegexes: Array.Empty<Regex>());

        filters.ShouldShow(MakePr(authorLogin: null)).Should().BeFalse();
        filters.ShouldShow(MakePr(authorLogin: "")).Should().BeFalse();
        filters.ShouldShow(MakePr(authorLogin: "  ")).Should().BeFalse();
        filters.ShouldShow(MakePr(authorLogin: "octo")).Should().BeTrue();
    }

    // ---------- Ignored / disappeared ----------

    [Fact]
    public void Ignored_pr_is_hidden_by_default()
        => DefaultFilters().ShouldShow(MakePr(isIgnored: true)).Should().BeFalse();

    [Fact]
    public void ShowIgnored_reveals_ignored_pr()
    {
        var filters = InboxFilters.From(
            showClosed: false, showIgnored: true,
            enabledSources: InboxFilters.KnownSourceClasses,
            excludedRepos: Array.Empty<string>(),
            excludedAuthors: Array.Empty<string>(),
            ignoredRepoRegexes: Array.Empty<Regex>());

        filters.ShouldShow(MakePr(isIgnored: true)).Should().BeTrue();
    }

    [Fact]
    public void Open_pr_with_disappearedAt_is_hidden_by_default()
    {
        DefaultFilters().ShouldShow(MakePr(
            status: PullRequestStatus.Open,
            disappearedAt: DateTimeOffset.UnixEpoch)).Should().BeFalse();
    }

    [Fact]
    public void ShowIgnored_reveals_open_pr_with_disappearedAt()
    {
        var filters = InboxFilters.From(
            showClosed: false, showIgnored: true,
            enabledSources: InboxFilters.KnownSourceClasses,
            excludedRepos: Array.Empty<string>(),
            excludedAuthors: Array.Empty<string>(),
            ignoredRepoRegexes: Array.Empty<Regex>());

        filters.ShouldShow(MakePr(
            status: PullRequestStatus.Open,
            disappearedAt: DateTimeOffset.UnixEpoch)).Should().BeTrue();
    }

    // ---------- IgnoredRepos regex ----------

    [Fact]
    public void IgnoredRepoRegex_hides_matching_rows()
    {
        var regexes = InboxFilters.CompileIgnoredRepoRegexes(new[] { "^contoso/sandbox-.*" });
        var filters = InboxFilters.From(
            showClosed: false, showIgnored: false,
            enabledSources: InboxFilters.KnownSourceClasses,
            excludedRepos: Array.Empty<string>(),
            excludedAuthors: Array.Empty<string>(),
            ignoredRepoRegexes: regexes);

        filters.ShouldShow(MakePr(displayRepo: "contoso/sandbox-alpha")).Should().BeFalse();
        filters.ShouldShow(MakePr(displayRepo: "contoso/prod-api")).Should().BeTrue();
    }

    [Fact]
    public void IgnoredRepoRegex_match_is_case_insensitive()
    {
        var regexes = InboxFilters.CompileIgnoredRepoRegexes(new[] { "^Sandbox/" });
        var filters = InboxFilters.From(
            showClosed: false, showIgnored: false,
            enabledSources: InboxFilters.KnownSourceClasses,
            excludedRepos: Array.Empty<string>(),
            excludedAuthors: Array.Empty<string>(),
            ignoredRepoRegexes: regexes);

        filters.ShouldShow(MakePr(displayRepo: "sandbox/anything")).Should().BeFalse();
    }

    [Fact]
    public void ShowIgnored_reveals_IgnoredRepoRegex_matches()
    {
        var regexes = InboxFilters.CompileIgnoredRepoRegexes(new[] { "^contoso/" });
        var filters = InboxFilters.From(
            showClosed: false, showIgnored: true,
            enabledSources: InboxFilters.KnownSourceClasses,
            excludedRepos: Array.Empty<string>(),
            excludedAuthors: Array.Empty<string>(),
            ignoredRepoRegexes: regexes);

        filters.ShouldShow(MakePr(displayRepo: "contoso/foo")).Should().BeTrue();
    }

    [Fact]
    public void Invalid_regex_is_dropped_quietly()
    {
        // Open `(` is a syntactically invalid pattern. The compiler should
        // skip it and the filter should still work for the other rules.
        var regexes = InboxFilters.CompileIgnoredRepoRegexes(new[] { "(", "^bad/" });
        regexes.Should().HaveCount(1);

        var filters = InboxFilters.From(
            showClosed: false, showIgnored: false,
            enabledSources: InboxFilters.KnownSourceClasses,
            excludedRepos: Array.Empty<string>(),
            excludedAuthors: Array.Empty<string>(),
            ignoredRepoRegexes: regexes);

        filters.ShouldShow(MakePr(displayRepo: "bad/repo")).Should().BeFalse();
        filters.ShouldShow(MakePr(displayRepo: "ok/repo")).Should().BeTrue();
    }

    [Fact]
    public void Empty_or_whitespace_pattern_strings_are_dropped()
        => InboxFilters.CompileIgnoredRepoRegexes(new[] { "", "   ", null! })
            .Should().BeEmpty();

    // ---------- Combinations (a small parity probe) ----------

    [Fact]
    public void Multiple_filters_combine_with_AND_semantics()
    {
        // Excluded repo + excluded author together: row hits both, but only
        // one match is enough to hide. Switching either off should reveal.
        var filtersBothOff = DefaultFilters();
        filtersBothOff.ShouldShow(MakePr(displayRepo: "foo/bar", authorLogin: "alice"))
            .Should().BeTrue();

        var filtersRepoExcluded = InboxFilters.From(
            showClosed: false, showIgnored: false,
            enabledSources: InboxFilters.KnownSourceClasses,
            excludedRepos: new[] { "foo/bar" },
            excludedAuthors: Array.Empty<string>(),
            ignoredRepoRegexes: Array.Empty<Regex>());
        filtersRepoExcluded.ShouldShow(MakePr(displayRepo: "foo/bar", authorLogin: "alice"))
            .Should().BeFalse();

        var filtersAuthorExcluded = InboxFilters.From(
            showClosed: false, showIgnored: false,
            enabledSources: InboxFilters.KnownSourceClasses,
            excludedRepos: Array.Empty<string>(),
            excludedAuthors: new[] { "alice" },
            ignoredRepoRegexes: Array.Empty<Regex>());
        filtersAuthorExcluded.ShouldShow(MakePr(displayRepo: "foo/bar", authorLogin: "alice"))
            .Should().BeFalse();
    }
}
