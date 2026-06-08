using System.Text.RegularExpressions;
using FluentAssertions;
using PrInbox.Core.Config;

namespace PrInbox.Tests.Config;

/// <summary>
/// Tests for <see cref="RepoPathScope"/> — the shared normalization + glob
/// matching that powers the monorepo per-repo path filter. The dialect is
/// deliberately tiny; these cases pin its behaviour so the Inbox filter and
/// the Settings editor can never drift from each other.
/// </summary>
public sealed class RepoPathScopeTests
{
    private static bool InScope(string path, params string[] patterns)
        => RepoPathScope.IsInScope(new[] { path }, RepoPathScope.CompilePatterns(patterns));

    // ---------- NormalizeRepoKey ----------

    [Theory]
    [InlineData("contoso/repo", "contoso/repo")]
    [InlineData("  contoso/repo  ", "contoso/repo")]
    [InlineData("/contoso/repo/", "contoso/repo")]
    [InlineData("contoso\\repo", "contoso/repo")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void NormalizeRepoKey_Trims_And_Normalizes(string? raw, string expected)
        => RepoPathScope.NormalizeRepoKey(raw).Should().Be(expected);

    // ---------- NormalizePath ----------

    [Theory]
    [InlineData("src/Foo.cs", "src/Foo.cs")]
    [InlineData("/src/Foo.cs", "src/Foo.cs")]
    [InlineData("src\\Foo.cs", "src/Foo.cs")]
    [InlineData("  src/Foo.cs  ", "src/Foo.cs")]
    public void NormalizePath_Strips_Leading_Slash_And_Backslashes(string raw, string expected)
        => RepoPathScope.NormalizePath(raw).Should().Be(expected);

    // ---------- Bare prefix: exact + subtree ----------

    [Theory]
    [InlineData("src/ServiceA")]                 // the folder itself
    [InlineData("src/ServiceA/Foo.cs")]          // a file directly under it
    [InlineData("src/ServiceA/sub/deep/Bar.cs")] // nested
    public void Bare_prefix_matches_folder_and_subtree(string path)
        => InScope(path, "src/ServiceA").Should().BeTrue();

    [Theory]
    [InlineData("src/ServiceAB/Foo.cs")]   // sibling that shares a prefix
    [InlineData("src/ServiceABC")]
    [InlineData("other/src/ServiceA/Foo.cs")]
    [InlineData("docs/ServiceA/Foo.cs")]
    public void Bare_prefix_does_not_match_prefix_siblings_or_unrelated(string path)
        => InScope(path, "src/ServiceA").Should().BeFalse();

    // ---------- Trailing slash / ** : subtree only ----------

    [Fact]
    public void Trailing_slash_matches_subtree_but_not_the_bare_folder()
    {
        InScope("src/ServiceA/Foo.cs", "src/ServiceA/").Should().BeTrue();
        InScope("src/ServiceA", "src/ServiceA/").Should().BeFalse();
    }

    [Fact]
    public void Trailing_double_star_matches_subtree_but_not_the_bare_folder()
    {
        InScope("src/Shared/Foo.cs", "src/Shared/**").Should().BeTrue();
        InScope("src/Shared", "src/Shared/**").Should().BeFalse();
    }

    // ---------- Wildcards ----------

    [Fact]
    public void Single_star_matches_one_segment_only()
    {
        InScope("src/foo/api", "src/*/api").Should().BeTrue();
        // '*' does not cross a '/', so a two-segment middle never matches.
        InScope("src/foo/bar/api", "src/*/api").Should().BeFalse();
    }

    [Fact]
    public void Single_star_pattern_is_exact_unless_subtree_requested()
    {
        // A wildcard pattern is anchored: it matches the exact path, not the
        // subtree. Use a trailing /** to capture everything inside.
        InScope("src/foo/api", "src/*/api").Should().BeTrue();
        InScope("src/foo/api/Controller.cs", "src/*/api").Should().BeFalse();
        InScope("src/foo/api/Controller.cs", "src/*/api/**").Should().BeTrue();
    }

    [Fact]
    public void Double_star_crosses_directory_separators()
    {
        InScope("src/a/b/c/Foo.cs", "src/**/Foo.cs").Should().BeTrue();
        InScope("src/Foo.cs", "src/**/Foo.cs").Should().BeFalse(); // ** segment requires a '/'
    }

    // ---------- Case sensitivity ----------

    [Fact]
    public void Path_matching_is_case_sensitive()
    {
        InScope("src/ServiceA/Foo.cs", "src/ServiceA").Should().BeTrue();
        InScope("SRC/servicea/Foo.cs", "src/ServiceA").Should().BeFalse();
    }

    // ---------- OR semantics across patterns ----------

    [Fact]
    public void Any_matching_pattern_puts_path_in_scope()
    {
        var globs = RepoPathScope.CompilePatterns(new[] { "src/A", "src/B" });
        RepoPathScope.IsInScope(new[] { "src/B/x.cs" }, globs).Should().BeTrue();
        RepoPathScope.IsInScope(new[] { "src/C/x.cs" }, globs).Should().BeFalse();
    }

    [Fact]
    public void Any_matching_path_puts_pr_in_scope()
    {
        var globs = RepoPathScope.CompilePatterns(new[] { "src/A" });
        // A PR touches many files; one in-scope file is enough.
        RepoPathScope.IsInScope(new[] { "docs/readme.md", "src/A/x.cs" }, globs).Should().BeTrue();
        RepoPathScope.IsInScope(new[] { "docs/readme.md", "tools/y.cs" }, globs).Should().BeFalse();
    }

    // ---------- Empty / unconfigured ----------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Blank_patterns_are_dropped(string? pattern)
        => RepoPathScope.CompilePatterns(new[] { pattern! }).Should().BeEmpty();

    [Fact]
    public void Empty_glob_list_is_never_in_scope()
        => RepoPathScope.IsInScope(new[] { "src/anything.cs" }, Array.Empty<Regex>())
            .Should().BeFalse();

    // ---------- CompileRepoScopes ----------

    [Fact]
    public void CompileRepoScopes_Keys_Are_Normalized_And_Case_Insensitive()
    {
        var scopes = RepoPathScope.CompileRepoScopes(new Dictionary<string, List<string>>
        {
            ["Contoso/MonoRepo"] = new() { "src/ServiceA" },
        });

        scopes.TryGetValue("contoso/monorepo", out var globs).Should().BeTrue();
        RepoPathScope.IsInScope(new[] { "src/ServiceA/Foo.cs" }, globs!).Should().BeTrue();
    }

    [Fact]
    public void CompileRepoScopes_Drops_Repos_With_No_Usable_Patterns()
    {
        var scopes = RepoPathScope.CompileRepoScopes(new Dictionary<string, List<string>>
        {
            ["contoso/configured"] = new() { "src/A" },
            ["contoso/blankonly"] = new() { "", "  " },
            ["contoso/empty"] = new(),
        });

        scopes.Should().ContainKey("contoso/configured");
        scopes.Should().NotContainKey("contoso/blankonly");
        scopes.Should().NotContainKey("contoso/empty");
    }

    [Fact]
    public void CompileRepoScopes_Null_Or_Empty_Config_Is_Empty()
    {
        RepoPathScope.CompileRepoScopes(null).Should().BeEmpty();
        RepoPathScope.CompileRepoScopes(new Dictionary<string, List<string>>()).Should().BeEmpty();
    }
}
