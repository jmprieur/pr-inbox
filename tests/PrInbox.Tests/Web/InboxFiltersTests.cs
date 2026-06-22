using System.Text.RegularExpressions;
using FluentAssertions;
using PrInbox.Core.Config;
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
    [InlineData("ado:fabrikam", "src-ado")]
    [InlineData("ado:contoso/foo", "src-ado")]
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
        filters.ShouldShow(MakePr(sourceId: "ado:fabrikam")).Should().BeFalse();
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
            Id = "ado:fabrikam/foo",
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
            Host = "ghe.example.com",
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

    // ---------- LabelForClass (badge text — must agree with the chip class) ----------

    [Theory]
    [InlineData("src-emu",    "EMU")]
    [InlineData("src-public", "public")]
    [InlineData("src-ghe",    "proxima")]
    [InlineData("src-ado",    "ado")]
    public void LabelForClass_maps_known_classes_to_visible_text(string chipClass, string expected)
        => InboxFilters.LabelForClass(chipClass, fallback: "unused").Should().Be(expected);

    [Fact]
    public void LabelForClass_unknown_class_returns_fallback()
        => InboxFilters.LabelForClass("src-other", fallback: "weird.host").Should().Be("weird.host");

    [Fact]
    public void Emu_identity_source_with_arbitrary_id_labels_as_EMU_not_public()
    {
        // The bug Jean-Marc hit on the new install: gh.com:jmprieur_microsoft
        // is classified as src-emu (correct, chip filter works) but the
        // legacy id-string label parser saw "starts with gh.com:" and
        // returned "public". Now the label is derived from the chip
        // class, so they're guaranteed to agree.
        var sc = new SourceConfig
        {
            Id = "gh.com:jmprieur_microsoft",
            Kind = SourceConfigKind.GitHub,
            Host = "github.com",
            Identity = "jmprieur_microsoft",
        };
        var chipClass = InboxFilters.ClassifyConfig(sc);
        chipClass.Should().Be("src-emu");
        InboxFilters.LabelForClass(chipClass, sc.Id).Should().Be("EMU");
    }

    [Fact]
    public void Personal_identity_source_labels_as_public_not_id()
    {
        var sc = new SourceConfig
        {
            Id = "gh.com:jmprieur",
            Kind = SourceConfigKind.GitHub,
            Host = "github.com",
            Identity = "jmprieur",
        };
        InboxFilters.LabelForClass(InboxFilters.ClassifyConfig(sc), sc.Id)
            .Should().Be("public");
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

    // ---------- ShowDone / IsMarkedDone semantics ----------

    private static InboxRow MakeInboxRow(
        string url = "https://github.com/o/r/pull/1",
        string? markedDoneHeadSha = null,
        string? currentHeadSha = null,
        DateTimeOffset? markedDoneAt = null,
        DateTimeOffset? flaggedAt = null,
        PullRequestStatus status = PullRequestStatus.Open,
        bool isIgnored = false,
        string displayRepo = "o/r",
        EnrichState enrichState = EnrichState.Enriched,
        IReadOnlyList<string>? touchedPaths = null,
        TouchedPathState touchedState = TouchedPathState.Unknown)
        => new(
            Url: url,
            DisplayRepo: displayRepo,
            Number: 1,
            Title: "T",
            AuthorLogin: "octo",
            SourceId: "gh.com",
            SourceKind: SourceKind.GitHub,
            IdentityUsed: "jmprieur",
            Status: status,
            EnrichState: enrichState,
            LastSyncedAt: DateTimeOffset.UnixEpoch,
            OpenThreadCount: 0,
            UnresolvedBotCount: 0,
            DriftKind: DriftKind.Unknown,
            DriftCount: 0,
            LastReviewedHeadSha: null,
            CurrentHeadSha: currentHeadSha,
            IsIgnored: isIgnored,
            MarkedDoneHeadSha: markedDoneHeadSha,
            MarkedDoneAt: markedDoneAt,
            FlaggedAt: flaggedAt,
            TouchedPaths: touchedPaths,
            TouchedPathState: touchedState);

    [Fact]
    public void InboxRow_IsMarkedDone_True_When_Sha_Matches_Current()
    {
        var row = MakeInboxRow(markedDoneHeadSha: "abc", currentHeadSha: "abc");
        row.IsMarkedDone.Should().BeTrue();
        row.ReactivatedSinceMarkedDone.Should().BeFalse();
    }

    [Fact]
    public void InboxRow_IsMarkedDone_False_When_Author_Pushed()
    {
        var row = MakeInboxRow(markedDoneHeadSha: "abc", currentHeadSha: "def");
        row.IsMarkedDone.Should().BeFalse();
        row.ReactivatedSinceMarkedDone.Should().BeTrue();
    }

    [Fact]
    public void InboxRow_IsMarkedDone_True_When_Current_Sha_Unknown()
    {
        // Defensive: a row whose snapshot hasn't landed yet should not
        // suddenly un-snooze just because CurrentHeadSha is null.
        var row = MakeInboxRow(markedDoneHeadSha: "abc", currentHeadSha: null);
        row.IsMarkedDone.Should().BeTrue();
        row.ReactivatedSinceMarkedDone.Should().BeFalse();
    }

    [Fact]
    public void InboxRow_IsMarkedDone_False_When_Never_Marked()
    {
        MakeInboxRow().IsMarkedDone.Should().BeFalse();
    }

    [Fact]
    public void ShowDone_False_Hides_Done_Rows_Shows_Active()
    {
        var filters = InboxFilters.From(
            showClosed: false, showIgnored: false, showDone: false,
            enabledSources: InboxFilters.KnownSourceClasses,
            excludedRepos: Array.Empty<string>(),
            excludedAuthors: Array.Empty<string>(),
            ignoredRepoRegexes: Array.Empty<Regex>());

        var done   = MakeInboxRow(markedDoneHeadSha: "abc", currentHeadSha: "abc");
        var active = MakeInboxRow();

        filters.ShouldShow(done).Should().BeFalse();
        filters.ShouldShow(active).Should().BeTrue();
    }

    [Fact]
    public void ShowDone_True_Reveals_Done_Rows()
    {
        var filters = InboxFilters.From(
            showClosed: false, showIgnored: false, showDone: true,
            enabledSources: InboxFilters.KnownSourceClasses,
            excludedRepos: Array.Empty<string>(),
            excludedAuthors: Array.Empty<string>(),
            ignoredRepoRegexes: Array.Empty<Regex>());

        var done = MakeInboxRow(markedDoneHeadSha: "abc", currentHeadSha: "abc");

        filters.ShouldShow(done).Should().BeTrue();
    }

    [Fact]
    public void Done_Row_Reappears_When_Author_Pushes_New_Sha()
    {
        // The whole point of the feature: snooze auto-clears on push.
        var filters = InboxFilters.From(
            showClosed: false, showIgnored: false, showDone: false,
            enabledSources: InboxFilters.KnownSourceClasses,
            excludedRepos: Array.Empty<string>(),
            excludedAuthors: Array.Empty<string>(),
            ignoredRepoRegexes: Array.Empty<Regex>());

        var reactivated = MakeInboxRow(markedDoneHeadSha: "abc", currentHeadSha: "def");

        reactivated.IsMarkedDone.Should().BeFalse();
        filters.ShouldShow(reactivated).Should().BeTrue();
    }

    // ---------- Flag / OnlyFlagged semantics ----------

    [Fact]
    public void InboxRow_IsFlagged_True_When_FlaggedAt_Set()
    {
        MakeInboxRow(flaggedAt: DateTimeOffset.UtcNow).IsFlagged.Should().BeTrue();
        MakeInboxRow().IsFlagged.Should().BeFalse();
    }

    [Fact]
    public void OnlyFlagged_True_Hides_Unflagged_Rows()
    {
        var filters = InboxFilters.From(
            showClosed: false, showIgnored: false, showDone: false, onlyFlagged: true,
            enabledSources: InboxFilters.KnownSourceClasses,
            excludedRepos: Array.Empty<string>(),
            excludedAuthors: Array.Empty<string>(),
            ignoredRepoRegexes: Array.Empty<Regex>());

        var flagged   = MakeInboxRow(flaggedAt: DateTimeOffset.UtcNow);
        var unflagged = MakeInboxRow();

        filters.ShouldShow(flagged).Should().BeTrue();
        filters.ShouldShow(unflagged).Should().BeFalse();
    }

    [Fact]
    public void OnlyFlagged_False_Default_Does_Not_Restrict()
    {
        var filters = InboxFilters.From(
            showClosed: false, showIgnored: false, showDone: false, onlyFlagged: false,
            enabledSources: InboxFilters.KnownSourceClasses,
            excludedRepos: Array.Empty<string>(),
            excludedAuthors: Array.Empty<string>(),
            ignoredRepoRegexes: Array.Empty<Regex>());

        filters.ShouldShow(MakeInboxRow()).Should().BeTrue();
        filters.ShouldShow(MakeInboxRow(flaggedAt: DateTimeOffset.UtcNow)).Should().BeTrue();
    }

    [Fact]
    public void Flag_Does_Not_Bypass_Done_Or_Closed_Filters()
    {
        // Orthogonality: a flagged-and-done PR is still hidden when
        // Show done is off and Show only flagged is off. The user must
        // either enable Show done OR enable Show only flagged.
        var filters = InboxFilters.From(
            showClosed: false, showIgnored: false, showDone: false, onlyFlagged: false,
            enabledSources: InboxFilters.KnownSourceClasses,
            excludedRepos: Array.Empty<string>(),
            excludedAuthors: Array.Empty<string>(),
            ignoredRepoRegexes: Array.Empty<Regex>());

        var flaggedAndDone = MakeInboxRow(
            flaggedAt: DateTimeOffset.UtcNow,
            markedDoneHeadSha: "abc",
            currentHeadSha: "abc");

        filters.ShouldShow(flaggedAndDone).Should().BeFalse();

        // Closed flagged row also stays hidden behind Show closed.
        var flaggedAndClosed = MakeInboxRow(
            flaggedAt: DateTimeOffset.UtcNow,
            status: PullRequestStatus.Merged);

        filters.ShouldShow(flaggedAndClosed).Should().BeFalse();
    }

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

    // ---------- Monorepo path scope ----------

    private static InboxFilters PathScopedFilters(
        Dictionary<string, List<string>> repoFilters,
        bool showOutOfScope = false)
        => InboxFilters.From(
            showClosed: false, showIgnored: false, showDone: true, onlyFlagged: false,
            enabledSources: InboxFilters.KnownSourceClasses,
            excludedRepos: Array.Empty<string>(),
            excludedAuthors: Array.Empty<string>(),
            ignoredRepoRegexes: Array.Empty<Regex>(),
            sourceConfigs: null,
            showOutOfScope: showOutOfScope,
            pathScopeByRepo: RepoPathScope.CompileRepoScopes(repoFilters));

    private static Dictionary<string, List<string>> Scope(string repo, params string[] globs)
        => new() { [repo] = new List<string>(globs) };

    [Fact]
    public void PathScope_Complete_In_Scope_Row_Is_Shown()
    {
        var filters = PathScopedFilters(Scope("o/r", "src/A"));
        var row = MakeInboxRow(
            touchedPaths: new[] { "src/A/Foo.cs" },
            touchedState: TouchedPathState.Complete);

        filters.ShouldShow(row).Should().BeTrue();
    }

    [Fact]
    public void PathScope_Complete_Out_Of_Scope_Row_Is_Hidden()
    {
        var filters = PathScopedFilters(Scope("o/r", "src/A"));
        var row = MakeInboxRow(
            touchedPaths: new[] { "docs/readme.md" },
            touchedState: TouchedPathState.Complete);

        filters.ShouldShow(row).Should().BeFalse();
    }

    [Fact]
    public void PathScope_Out_Of_Scope_Row_Is_Shown_When_ShowOutOfScope()
    {
        var filters = PathScopedFilters(Scope("o/r", "src/A"), showOutOfScope: true);
        var row = MakeInboxRow(
            touchedPaths: new[] { "docs/readme.md" },
            touchedState: TouchedPathState.Complete);

        filters.ShouldShow(row).Should().BeTrue();
    }

    [Theory]
    [InlineData(TouchedPathState.Unknown)]
    [InlineData(TouchedPathState.Unavailable)]
    [InlineData(TouchedPathState.Truncated)]
    public void PathScope_Fails_Open_For_NonComplete_States(TouchedPathState state)
    {
        // Even though none of these touch src/A, only a COMPLETE file list
        // may hide a row. Unknown / Unavailable / Truncated always show.
        var filters = PathScopedFilters(Scope("o/r", "src/A"));
        var row = MakeInboxRow(
            touchedPaths: state == TouchedPathState.Unknown ? null : new[] { "docs/readme.md" },
            touchedState: state);

        filters.ShouldShow(row).Should().BeTrue();
    }

    [Fact]
    public void PathScope_Repo_Without_Filter_Is_Unaffected()
    {
        var filters = PathScopedFilters(Scope("o/r", "src/A"));
        // Different repo: no configured scope -> always shown regardless of paths.
        var row = MakeInboxRow(
            displayRepo: "other/repo",
            touchedPaths: new[] { "anywhere/else.cs" },
            touchedState: TouchedPathState.Complete);

        filters.ShouldShow(row).Should().BeTrue();
    }

    [Fact]
    public void PathScope_Repo_Key_Match_Is_Case_Insensitive()
    {
        var filters = PathScopedFilters(Scope("O/R", "src/A"));
        var row = MakeInboxRow(
            displayRepo: "o/r",
            touchedPaths: new[] { "docs/readme.md" },
            touchedState: TouchedPathState.Complete);

        filters.ShouldShow(row).Should().BeFalse();
    }

    [Fact]
    public void PathScope_Is_Never_Applied_Sync_Side()
    {
        // The PullRequestRow overload feeds the enrichment prioritizer.
        // It must NEVER path-filter: sync candidates have no file data, and
        // hiding them would deprioritize their enrichment -> they'd never
        // get files -> permanently hidden (the deadlock this design avoids).
        var filters = PathScopedFilters(Scope("o/r", "src/A"));
        var pr = MakePr(displayRepo: "o/r");

        filters.ShouldShow(pr).Should().BeTrue();
    }

    // ---------- InboxRow.ClassifyTouchedPaths ----------

    [Fact]
    public void Classify_Basic_Is_Unknown()
    {
        var (paths, state) = InboxRow.ClassifyTouchedPaths(EnrichState.Basic, files: null);
        state.Should().Be(TouchedPathState.Unknown);
        paths.Should().BeNull();
    }

    [Fact]
    public void Classify_Basic_With_Files_Is_Still_Unknown()
    {
        // An unenriched row's snapshot files (if any) aren't authoritative yet.
        var files = new[] { new SnapshotFileChange("src/A/x.cs", 1, 0, "modified") };
        var (paths, state) = InboxRow.ClassifyTouchedPaths(EnrichState.Basic, files);
        state.Should().Be(TouchedPathState.Unknown);
        paths.Should().BeNull();
    }

    [Fact]
    public void Classify_Enriched_Null_Files_Is_Unavailable()
    {
        var (paths, state) = InboxRow.ClassifyTouchedPaths(EnrichState.Enriched, files: null);
        state.Should().Be(TouchedPathState.Unavailable);
        paths.Should().BeNull();
    }

    [Fact]
    public void Classify_Enriched_Empty_Or_Blank_Files_Is_Unavailable()
    {
        // A zero-file PR, or files with no usable path, can't be scoped.
        // Fail open (Unavailable) rather than hide on an empty signal.
        InboxRow.ClassifyTouchedPaths(EnrichState.Enriched, Array.Empty<SnapshotFileChange>())
            .State.Should().Be(TouchedPathState.Unavailable);

        var blank = new[] { new SnapshotFileChange("   ", 0, 0, null) };
        InboxRow.ClassifyTouchedPaths(EnrichState.Enriched, blank)
            .State.Should().Be(TouchedPathState.Unavailable);
    }

    [Fact]
    public void Classify_Enriched_With_Files_Is_Complete_And_Extracts_Paths()
    {
        var files = new[]
        {
            new SnapshotFileChange("src/A/x.cs", 1, 0, "modified"),
            new SnapshotFileChange("docs/readme.md", 2, 1, "modified"),
        };
        var (paths, state) = InboxRow.ClassifyTouchedPaths(EnrichState.Enriched, files);
        state.Should().Be(TouchedPathState.Complete);
        paths.Should().BeEquivalentTo(new[] { "src/A/x.cs", "docs/readme.md" });
    }

    [Fact]
    public void Classify_Enriched_At_File_Cap_Is_Truncated()
    {
        var files = Enumerable
            .Range(0, InboxRow.GitHubChangedFileCap)
            .Select(i => new SnapshotFileChange($"src/f{i}.cs", 1, 0, "modified"))
            .ToList();
        var (paths, state) = InboxRow.ClassifyTouchedPaths(EnrichState.Enriched, files);
        state.Should().Be(TouchedPathState.Truncated);
        paths.Should().BeNull();
    }

    [Fact]
    public void FromRow_Populates_TouchedPaths_From_Snapshot_Files()
    {
        var pr = MakePr(displayRepo: "o/r");
        var files = new[] { new SnapshotFileChange("src/A/x.cs", 1, 0, "modified") };

        var row = InboxRow.FromRow(pr, openThreads: 0, unresolvedBot: 0, snapshotFiles: files);

        row.TouchedPathState.Should().Be(TouchedPathState.Complete);
        row.TouchedPaths.Should().BeEquivalentTo(new[] { "src/A/x.cs" });
    }
}
