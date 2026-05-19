using System.Text.RegularExpressions;
using FluentAssertions;
using PrInbox.Core.Credentials;
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
    [InlineData("gh.com", "src-public")]
    [InlineData("gh.com:public", "src-public")]
    [InlineData("gh.com:work", "src-public")]
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
    public void Partial_chip_set_keeps_unknown_sources_visible()
    {
        // Defense-in-depth: a row whose class is "src-other" can't be
        // toggled (no chip exists for it), so it must remain visible even
        // when the user has actively unchecked one or more chips. Without
        // this, a user with a single source whose id maps to "src-other"
        // would see ALL rows vanish the moment they uncheck any chip —
        // which is exactly the bug reported on the bare "gh.com" id
        // before SourceClassOf was extended to recognise it.
        var filters = InboxFilters.From(
            showClosed: false, showIgnored: false,
            enabledSources: new[] { "src-public", "src-emu", "src-ghe" }, // ado off
            excludedRepos: Array.Empty<string>(),
            excludedAuthors: Array.Empty<string>(),
            ignoredRepoRegexes: Array.Empty<Regex>());

        filters.ShouldShow(MakePr(sourceId: "unknown:thing")).Should().BeTrue();
    }

    [Fact]
    public void Bare_gh_com_source_stays_visible_when_other_chip_unchecked()
    {
        // Regression: ConfigService.DefaultIdFor writes the bare id
        // "gh.com" when the user clicks "+ Add GitHub.com" in Settings.
        // Without the new SourceClassOf branch this fell through to
        // src-other, which combined with the old "any non-listed class
        // is hidden under partial chips" rule meant unchecking ANY of
        // EMU/proxima/ADO made the user's entire inbox disappear.
        var filters = InboxFilters.From(
            showClosed: false, showIgnored: false,
            enabledSources: new[] { "src-public", "src-ghe", "src-ado" }, // EMU off
            excludedRepos: Array.Empty<string>(),
            excludedAuthors: Array.Empty<string>(),
            ignoredRepoRegexes: Array.Empty<Regex>());

        filters.ShouldShow(MakePr(sourceId: "gh.com")).Should().BeTrue();
    }

    [Fact]
    public void Bare_gh_com_source_hidden_when_public_chip_unchecked()
    {
        // Counterpart to the regression test above: when the user
        // explicitly unchecks "public", the bare gh.com row IS hidden,
        // because gh.com now maps to src-public.
        var filters = InboxFilters.From(
            showClosed: false, showIgnored: false,
            enabledSources: new[] { "src-emu", "src-ghe", "src-ado" }, // public off
            excludedRepos: Array.Empty<string>(),
            excludedAuthors: Array.Empty<string>(),
            ignoredRepoRegexes: Array.Empty<Regex>());

        filters.ShouldShow(MakePr(sourceId: "gh.com")).Should().BeFalse();
    }

    // ---------- ClassifyConfig (host+identity-based classification) ----------

    [Fact]
    public void ClassifyConfig_ado_is_always_src_ado()
        => InboxFilters.ClassifyConfig(new SourceConfig
        {
            Id = "ado:mseng/foo",
            Kind = SourceConfigKind.AzureDevOps,
            Host = null,
            Identity = "default",
        }).Should().Be("src-ado");

    [Fact]
    public void ClassifyConfig_ghe_is_always_src_ghe()
        => InboxFilters.ClassifyConfig(new SourceConfig
        {
            Id = "ghe.microsoft",
            Kind = SourceConfigKind.GitHubEnterprise,
            Host = "microsoft.ghe.com",
            Identity = "jmprieur_microsoft",
        }).Should().Be("src-ghe");

    [Fact]
    public void ClassifyConfig_github_default_identity_is_public()
        => InboxFilters.ClassifyConfig(new SourceConfig
        {
            Id = "gh.com",
            Kind = SourceConfigKind.GitHub,
            Host = "github.com",
            Identity = "default",
        }).Should().Be("src-public");

    [Theory]
    [InlineData("jmprieur")]                  // personal login, no underscore
    [InlineData("octo")]                      // personal login, no underscore
    [InlineData("")]                          // missing identity
    [InlineData(null)]                        // null identity
    public void ClassifyConfig_github_personal_identity_is_public(string? identity)
        => InboxFilters.ClassifyConfig(new SourceConfig
        {
            Id = "gh.com",
            Kind = SourceConfigKind.GitHub,
            Host = "github.com",
            Identity = identity ?? string.Empty,
        }).Should().Be("src-public");

    [Theory]
    [InlineData("jmprieur_microsoft")]
    [InlineData("rcastiglione_microsoft")]
    [InlineData("anyone_someorg")]
    public void ClassifyConfig_github_emu_identity_is_emu(string identity)
        => InboxFilters.ClassifyConfig(new SourceConfig
        {
            Id = $"gh.com:{identity}",
            Kind = SourceConfigKind.GitHub,
            Host = "github.com",
            Identity = identity,
        }).Should().Be("src-emu");

    [Fact]
    public void Two_github_identities_classify_as_emu_and_public()
    {
        // Jenny's case: two github.com sources, one personal + one EMU.
        // The config-derived classifier distinguishes them by identity
        // even though both share the same Kind=GitHub and Host=github.com.
        var personalSrc = new SourceConfig
        {
            Id = "gh.com",
            Kind = SourceConfigKind.GitHub,
            Host = "github.com",
            Identity = "jenny",
        };
        var emuSrc = new SourceConfig
        {
            Id = "gh.com:jenny_microsoft",
            Kind = SourceConfigKind.GitHub,
            Host = "github.com",
            Identity = "jenny_microsoft",
        };

        // public chip off, EMU chip on → only the EMU rows survive.
        var filtersEmuOnly = InboxFilters.From(
            showClosed: false, showIgnored: false,
            enabledSources: new[] { "src-emu", "src-ghe", "src-ado" }, // public off
            excludedRepos: Array.Empty<string>(),
            excludedAuthors: Array.Empty<string>(),
            ignoredRepoRegexes: Array.Empty<Regex>(),
            sourceConfigs: new[] { personalSrc, emuSrc });

        filtersEmuOnly.ShouldShow(MakePr(sourceId: "gh.com")).Should().BeFalse();
        filtersEmuOnly.ShouldShow(MakePr(sourceId: "gh.com:jenny_microsoft")).Should().BeTrue();

        // EMU chip off, public chip on → mirror image.
        var filtersPublicOnly = InboxFilters.From(
            showClosed: false, showIgnored: false,
            enabledSources: new[] { "src-public", "src-ghe", "src-ado" }, // emu off
            excludedRepos: Array.Empty<string>(),
            excludedAuthors: Array.Empty<string>(),
            ignoredRepoRegexes: Array.Empty<Regex>(),
            sourceConfigs: new[] { personalSrc, emuSrc });

        filtersPublicOnly.ShouldShow(MakePr(sourceId: "gh.com")).Should().BeTrue();
        filtersPublicOnly.ShouldShow(MakePr(sourceId: "gh.com:jenny_microsoft")).Should().BeFalse();
    }

    [Fact]
    public void Config_based_classifier_overrides_legacy_id_parser()
    {
        // Reverse case: an id whose string-shape would classify one way
        // via the legacy SourceClassOf, but whose actual SourceConfig
        // says something else. The config wins.
        // Example: a SourceConfig with id "gh.com" but explicitly tagged
        // as an EMU identity. The legacy parser maps "gh.com" → public,
        // but the config says EMU — the config should win.
        var emuSrcWithBareId = new SourceConfig
        {
            Id = "gh.com",
            Kind = SourceConfigKind.GitHub,
            Host = "github.com",
            Identity = "jenny_microsoft",
        };

        var filters = InboxFilters.From(
            showClosed: false, showIgnored: false,
            enabledSources: new[] { "src-emu", "src-ghe", "src-ado" }, // public off
            excludedRepos: Array.Empty<string>(),
            excludedAuthors: Array.Empty<string>(),
            ignoredRepoRegexes: Array.Empty<Regex>(),
            sourceConfigs: new[] { emuSrcWithBareId });

        // Public is OFF, EMU is ON. Config says this id is EMU →
        // should still be visible.
        filters.ShouldShow(MakePr(sourceId: "gh.com")).Should().BeTrue();
    }

    [Fact]
    public void Row_with_no_matching_config_falls_back_to_legacy_parser()
    {
        // Rows surviving a source-config removal (or test fixtures that
        // never had a config) should still classify via SourceClassOf.
        var filters = InboxFilters.From(
            showClosed: false, showIgnored: false,
            enabledSources: new[] { "src-emu", "src-ghe", "src-ado" }, // public off
            excludedRepos: Array.Empty<string>(),
            excludedAuthors: Array.Empty<string>(),
            ignoredRepoRegexes: Array.Empty<Regex>(),
            sourceConfigs: Array.Empty<SourceConfig>()); // empty config

        // Legacy classification: "gh.com" → src-public → public is off → hidden.
        filters.ShouldShow(MakePr(sourceId: "gh.com")).Should().BeFalse();
        // Legacy classification: "ado:foo" → src-ado → ado is on → visible.
        filters.ShouldShow(MakePr(sourceId: "ado:foo")).Should().BeTrue();
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
    public void Open_pr_with_disappearedAt_is_visible_by_default()
    {
        // Behavior change vs. prior versions: disappeared PRs surface in
        // the main inbox with a "no longer assigned" chip so the user can
        // still see them. Only explicit ignore (per-PR or regex) hides.
        DefaultFilters().ShouldShow(MakePr(
            status: PullRequestStatus.Open,
            disappearedAt: DateTimeOffset.UnixEpoch)).Should().BeTrue();
    }

    [Fact]
    public void Closed_pr_with_disappearedAt_is_still_hidden_by_status()
    {
        // The disappear chip is open-only. A closed/merged PR that's also
        // disappeared is still hidden by the !ShowClosed rule.
        DefaultFilters().ShouldShow(MakePr(
            status: PullRequestStatus.Closed,
            disappearedAt: DateTimeOffset.UnixEpoch)).Should().BeFalse();
    }

    [Fact]
    public void Ignored_pr_with_disappearedAt_is_still_hidden_by_isIgnored()
    {
        // If the user explicitly ignored a PR AND it later disappeared,
        // the explicit ignore wins — keep it hidden.
        DefaultFilters().ShouldShow(MakePr(
            status: PullRequestStatus.Open,
            isIgnored: true,
            disappearedAt: DateTimeOffset.UnixEpoch)).Should().BeFalse();
    }

    [Fact]
    public void ShowIgnored_keeps_disappeared_pr_visible()
    {
        // ShowIgnored=true should be a strict superset — disappeared still
        // visible (because they were already visible at the default).
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

    // ---------- PartitionCandidates ----------
    //
    // The host (InboxSyncHostedService) uses this to split enrich
    // candidates into "visible" (refresh first) and "hidden" (refresh
    // second). The partition MUST agree with ShouldShow on every row or
    // visible-first prioritization stops actually prioritizing what's on
    // screen, defeating the whole feature.

    [Fact]
    public void PartitionCandidates_Splits_By_ShouldShow()
    {
        var visibleRow = MakePr(displayRepo: "owner/keep");
        var excludedByRepo = MakePr(displayRepo: "owner/hide");
        var excludedByAuthor = MakePr(displayRepo: "owner/keep", authorLogin: "bot");
        var ignoredRow = MakePr(displayRepo: "owner/keep", isIgnored: true);

        var filters = InboxFilters.From(
            showClosed: false, showIgnored: false,
            enabledSources: InboxFilters.KnownSourceClasses,
            excludedRepos: new[] { "owner/hide" },
            excludedAuthors: new[] { "bot" },
            ignoredRepoRegexes: Array.Empty<Regex>());

        var (visible, hidden) = PrInbox.Web.Services.InboxSyncHostedService.PartitionCandidates(
            new[] { visibleRow, excludedByRepo, excludedByAuthor, ignoredRow },
            filters);

        visible.Should().ContainSingle().Which.Should().Be(visibleRow);
        hidden.Should().BeEquivalentTo(new[] { excludedByRepo, excludedByAuthor, ignoredRow });
    }

    [Fact]
    public void PartitionCandidates_Empty_Input_Returns_Empty_Both()
    {
        var (visible, hidden) = PrInbox.Web.Services.InboxSyncHostedService.PartitionCandidates(
            Array.Empty<PullRequestRow>(),
            DefaultFilters());

        visible.Should().BeEmpty();
        hidden.Should().BeEmpty();
    }

    [Fact]
    public void PartitionCandidates_All_Visible_When_No_Exclusions()
    {
        var rows = new[]
        {
            MakePr(displayRepo: "a/a"),
            MakePr(displayRepo: "b/b"),
            MakePr(displayRepo: "c/c"),
        };

        var (visible, hidden) = PrInbox.Web.Services.InboxSyncHostedService.PartitionCandidates(
            rows, DefaultFilters());

        visible.Should().BeEquivalentTo(rows);
        hidden.Should().BeEmpty();
    }
}
