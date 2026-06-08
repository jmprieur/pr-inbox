using FluentAssertions;
using PrInbox.Core.Config;
using PrInbox.Core.Credentials;
using PrInbox.Core.Models;
using PrInbox.Core.Storage;
using PrInbox.Web.Services;

namespace PrInbox.Tests.Web;

/// <summary>
/// Locks the user-facing behaviors of <see cref="DoctorService"/>:
///
///   1. <b>Runtime enrichment</b> — open-PR counts and last-sync
///      timestamps land on the right source row. Per-source grouping
///      uses case-insensitive SourceId match (matches the rest of the
///      stack).
///   2. <b>EMU / active-login chip surfacing</b> — when the gh probe
///      finds an identity matching a source's bound login, the
///      EnrichedSourceCheck carries IsEmu + IsActiveGhLogin.
///   3. <b>Double-fetch detection</b> — the marquee advisory. Fires
///      when a default-identity gh.com source AND an explicit-identity
///      source for the currently-active gh login both exist. Does NOT
///      fire if either piece is missing, or if the explicit source is
///      bound to a non-active login.
///
/// Uses real in-memory SQLite + minimal stubs for IConfigService and
/// IGitHubAuthDiscovery so the SQL aggregation paths get exercised
/// for real instead of being mocked away.
/// </summary>
public class DoctorServiceTests : IAsyncLifetime
{
    private PrInboxDb _db = null!;
    private Microsoft.Data.Sqlite.SqliteConnection _keepAlive = null!;
    private PullRequestRepository _prs = null!;
    private SyncRunRepository _syncRuns = null!;

    public async Task InitializeAsync()
    {
        var conn = PrInboxDb.InMemoryConnectionString($"doctor-{Guid.NewGuid():N}");
        _db = new PrInboxDb(conn);
        _keepAlive = await _db.OpenAsync();
        await new MigrationRunner().MigrateAsync(conn);
        _prs = new PullRequestRepository(_db);
        _syncRuns = new SyncRunRepository(_db);
    }

    public async Task DisposeAsync()
    {
        await _keepAlive.DisposeAsync();
    }

    // ---------- Double-fetch detection ----------

    [Fact]
    public async Task Advisory_Fires_When_Default_And_Explicit_For_Active_Login_Coexist()
    {
        var sources = new[]
        {
            // default-identity gh.com source — fetches PRs for whichever gh login is active
            OkCheck("gh.com", "default"),
            // explicit-identity source for the SAME login that gh reports as active
            OkCheck("gh.com:jmprieur", "jmprieur"),
        };
        var configSvc = new StubConfigSvc(BaseReport(sources, allOk: true));
        var ghDiscovery = new StubGhDiscovery(new[]
        {
            new GitHubAuthIdentity("jmprieur", IsActive: true),
            new GitHubAuthIdentity("jmprieur_microsoft", IsActive: false),
        });
        var doctor = new DoctorService(configSvc, _prs, _db, ghDiscovery, new StubRateLimitProbe());

        var report = await doctor.RunAsync();

        report.Advisories.Should().ContainSingle()
            .Which.Severity.Should().Be(DoctorAdvisorySeverity.Warning);
        report.Advisories[0].Title.Should().Contain("Double-fetch");
        report.Advisories[0].Detail.Should().Contain("jmprieur");
        report.Advisories[0].Suggestion.Should().Contain("gh.com");

        // One-click fix: each default source becomes a BindToIdentity action
        // targeting the active gh login (the one the explicit source already
        // covers, so the bind will collapse to RemovedDuplicate).
        report.Advisories[0].Actions.Should().NotBeNull();
        report.Advisories[0].Actions!.Should().ContainSingle();
        var action = report.Advisories[0].Actions![0];
        action.Kind.Should().Be(DoctorAdvisoryActionKind.BindToIdentity);
        action.SourceId.Should().Be("gh.com");
        action.TargetIdentity.Should().Be("jmprieur");
    }

    [Fact]
    public async Task Advisory_Does_Not_Fire_When_Explicit_Source_Is_Not_The_Active_Login()
    {
        // Default + explicit-EMU coexist but EMU is NOT the active gh login,
        // so there's no actual double-fetch (default fetches public, explicit
        // fetches EMU — separate PRs).
        var sources = new[]
        {
            OkCheck("gh.com", "default"),
            OkCheck("gh.com:jmprieur_microsoft", "jmprieur_microsoft"),
        };
        var configSvc = new StubConfigSvc(BaseReport(sources, allOk: true));
        var ghDiscovery = new StubGhDiscovery(new[]
        {
            new GitHubAuthIdentity("jmprieur", IsActive: true),
            new GitHubAuthIdentity("jmprieur_microsoft", IsActive: false),
        });
        var doctor = new DoctorService(configSvc, _prs, _db, ghDiscovery, new StubRateLimitProbe());

        var report = await doctor.RunAsync();

        report.Advisories.Should().BeEmpty();
    }

    [Fact]
    public async Task Advisory_Does_Not_Fire_With_Only_Explicit_Sources()
    {
        var sources = new[]
        {
            OkCheck("gh.com:jmprieur", "jmprieur"),
            OkCheck("gh.com:jmprieur_microsoft", "jmprieur_microsoft"),
        };
        var configSvc = new StubConfigSvc(BaseReport(sources, allOk: true));
        var ghDiscovery = new StubGhDiscovery(new[]
        {
            new GitHubAuthIdentity("jmprieur", IsActive: true),
        });
        var doctor = new DoctorService(configSvc, _prs, _db, ghDiscovery, new StubRateLimitProbe());

        var report = await doctor.RunAsync();

        report.Advisories.Should().BeEmpty();
    }

    [Fact]
    public async Task Advisory_Does_Not_Fire_With_Only_Default_Source()
    {
        var sources = new[] { OkCheck("gh.com", "default") };
        var configSvc = new StubConfigSvc(BaseReport(sources, allOk: true));
        var ghDiscovery = new StubGhDiscovery(new[]
        {
            new GitHubAuthIdentity("jmprieur", IsActive: true),
        });
        var doctor = new DoctorService(configSvc, _prs, _db, ghDiscovery, new StubRateLimitProbe());

        var report = await doctor.RunAsync();

        report.Advisories.Should().BeEmpty();
    }

    // ---------- EMU / active chip surfacing ----------

    [Fact]
    public async Task Enriched_Source_Carries_Emu_And_Active_Flags_From_Gh_Discovery()
    {
        var sources = new[]
        {
            OkCheck("gh.com:jmprieur", "jmprieur"),
            OkCheck("gh.com:jmprieur_microsoft", "jmprieur_microsoft"),
        };
        var configSvc = new StubConfigSvc(BaseReport(sources, allOk: true));
        var ghDiscovery = new StubGhDiscovery(new[]
        {
            new GitHubAuthIdentity("jmprieur", IsActive: true),                  // public + active
            new GitHubAuthIdentity("jmprieur_microsoft", IsActive: false),       // EMU (underscore) + not active
        });
        var doctor = new DoctorService(configSvc, _prs, _db, ghDiscovery, new StubRateLimitProbe());

        var report = await doctor.RunAsync();

        var personal = report.Sources.Single(s => s.Base.Id == "gh.com:jmprieur");
        personal.IsEmu.Should().Be(false);
        personal.IsActiveGhLogin.Should().BeTrue();

        var emu = report.Sources.Single(s => s.Base.Id == "gh.com:jmprieur_microsoft");
        emu.IsEmu.Should().Be(true);
        emu.IsActiveGhLogin.Should().BeFalse();
    }

    [Fact]
    public async Task Enriched_Source_Has_Null_Emu_Flag_For_Default_Identity()
    {
        var sources = new[] { OkCheck("gh.com", "default") };
        var configSvc = new StubConfigSvc(BaseReport(sources, allOk: true));
        var ghDiscovery = new StubGhDiscovery(new[]
        {
            new GitHubAuthIdentity("jmprieur", IsActive: true),
        });
        var doctor = new DoctorService(configSvc, _prs, _db, ghDiscovery, new StubRateLimitProbe());

        var report = await doctor.RunAsync();

        // Default identity isn't bound to a specific login — EMU detection
        // would be ambiguous, so we leave it null rather than guess.
        var row = report.Sources.Single();
        row.IsEmu.Should().BeNull();
        row.IsActiveGhLogin.Should().BeFalse();
    }

    // ---------- Failed-sync advisory ----------

    [Fact]
    public async Task Advisory_Fires_When_Last_Sync_Failed_With_Retry_Action()
    {
        // One source, last run = Failed. Advisory should appear with a
        // RetrySync action pointing at the right source id.
        var sources = new[] { OkCheck("gh.com:jenny", "jenny") };
        var configSvc = new StubConfigSvc(BaseReport(sources, allOk: true));
        var ghDiscovery = new StubGhDiscovery(Array.Empty<GitHubAuthIdentity>());

        var runId = await _syncRuns.StartAsync("gh.com:jenny", "jenny", CancellationToken.None);
        await _syncRuns.CompleteAsync(runId, SyncRunStatus.Failed, prsSeen: 0,
            error: "HTTP 502 from api.github.com", ct: CancellationToken.None);

        var doctor = new DoctorService(configSvc, _prs, _db, ghDiscovery, new StubRateLimitProbe());
        var report = await doctor.RunAsync();

        var failedAdvisory = report.Advisories.SingleOrDefault(a => a.Title.StartsWith("Last sync failed"));
        failedAdvisory.Should().NotBeNull();
        failedAdvisory!.Severity.Should().Be(DoctorAdvisorySeverity.Warning);
        failedAdvisory.Detail.Should().Contain("HTTP 502");
        failedAdvisory.Actions.Should().NotBeNull();
        failedAdvisory.Actions!.Should().ContainSingle()
            .Which.Kind.Should().Be(DoctorAdvisoryActionKind.RetrySync);
        failedAdvisory.Actions![0].SourceId.Should().Be("gh.com:jenny");
    }

    [Fact]
    public async Task Failed_Sync_Advisory_Does_Not_Fire_When_Last_Sync_Succeeded()
    {
        var sources = new[] { OkCheck("gh.com:jenny", "jenny") };
        var configSvc = new StubConfigSvc(BaseReport(sources, allOk: true));
        var ghDiscovery = new StubGhDiscovery(Array.Empty<GitHubAuthIdentity>());

        var runId = await _syncRuns.StartAsync("gh.com:jenny", "jenny", CancellationToken.None);
        await _syncRuns.CompleteAsync(runId, SyncRunStatus.Ok, prsSeen: 5,
            error: null, ct: CancellationToken.None);

        var doctor = new DoctorService(configSvc, _prs, _db, ghDiscovery, new StubRateLimitProbe());
        var report = await doctor.RunAsync();

        report.Advisories.Should().NotContain(a => a.Title.StartsWith("Last sync failed"));
    }

    // ---------- Missing-scopes advisory ----------

    [Fact]
    public async Task Advisory_Fires_When_Required_Scopes_Are_Missing()
    {
        // Identity present, scopes don't include "repo".
        var sources = new[] { OkCheck("gh.com:jenny", "jenny") };
        var configSvc = new StubConfigSvc(BaseReport(sources, allOk: true));
        var ghDiscovery = new StubGhDiscovery(new[]
        {
            new GitHubAuthIdentity("jenny", IsActive: true) { Scopes = new[] { "gist", "read:org" } },
        });

        var doctor = new DoctorService(configSvc, _prs, _db, ghDiscovery, new StubRateLimitProbe());
        var report = await doctor.RunAsync();

        var scopeAdvisory = report.Advisories.SingleOrDefault(a => a.Title.StartsWith("Missing token scopes"));
        scopeAdvisory.Should().NotBeNull();
        scopeAdvisory!.Detail.Should().Contain("repo");
        scopeAdvisory.Suggestion.Should().Contain("gh auth refresh");
        scopeAdvisory.Suggestion.Should().Contain("-s repo");
    }

    [Fact]
    public async Task Scopes_Advisory_Does_Not_Fire_When_Parser_Reports_Empty_Scopes()
    {
        // Empty scopes list = parser didn't see the line (older gh). We
        // treat that as unknown, not as missing — better to under-warn.
        var sources = new[] { OkCheck("gh.com:jenny", "jenny") };
        var configSvc = new StubConfigSvc(BaseReport(sources, allOk: true));
        var ghDiscovery = new StubGhDiscovery(new[]
        {
            new GitHubAuthIdentity("jenny", IsActive: true),
        });

        var doctor = new DoctorService(configSvc, _prs, _db, ghDiscovery, new StubRateLimitProbe());
        var report = await doctor.RunAsync();

        report.Advisories.Should().NotContain(a => a.Title.StartsWith("Missing token scopes"));
    }

    [Fact]
    public async Task Scopes_Advisory_Does_Not_Fire_When_All_Required_Scopes_Present()
    {
        var sources = new[] { OkCheck("gh.com:jenny", "jenny") };
        var configSvc = new StubConfigSvc(BaseReport(sources, allOk: true));
        var ghDiscovery = new StubGhDiscovery(new[]
        {
            new GitHubAuthIdentity("jenny", IsActive: true)
                { Scopes = new[] { "repo", "read:org", "workflow" } },
        });

        var doctor = new DoctorService(configSvc, _prs, _db, ghDiscovery, new StubRateLimitProbe());
        var report = await doctor.RunAsync();

        report.Advisories.Should().NotContain(a => a.Title.StartsWith("Missing token scopes"));
    }

    [Fact]
    public async Task Scopes_Advisory_Dedupes_When_Multiple_Sources_Share_One_Login()
    {
        // Two sources bound to the same login should only produce one
        // scopes advisory — the missing scope is a property of the
        // token, not the source.
        var sources = new[]
        {
            OkCheck("gh.com:jenny#a", "jenny"),
            OkCheck("gh.com:jenny#b", "jenny"),
        };
        var configSvc = new StubConfigSvc(BaseReport(sources, allOk: true));
        var ghDiscovery = new StubGhDiscovery(new[]
        {
            new GitHubAuthIdentity("jenny", IsActive: true) { Scopes = new[] { "gist" } },
        });

        var doctor = new DoctorService(configSvc, _prs, _db, ghDiscovery, new StubRateLimitProbe());
        var report = await doctor.RunAsync();

        report.Advisories.Count(a => a.Title.StartsWith("Missing token scopes"))
            .Should().Be(1);
    }

    // ---------- Rate-limit advisory ----------

    [Fact]
    public async Task Advisory_Fires_When_Rate_Limit_Below_Threshold()
    {
        // 100/5000 = 2%, well under the 15% threshold.
        var sources = new[] { OkCheck("gh.com:jenny", "jenny") };
        var configSvc = new StubConfigSvc(BaseReport(sources, allOk: true));
        var ghDiscovery = new StubGhDiscovery(Array.Empty<GitHubAuthIdentity>());
        var probe = new StubRateLimitProbe(
            new RateLimitSnapshot(Remaining: 100, Limit: 5000,
                ResetAt: DateTimeOffset.UtcNow.AddMinutes(20)));

        var doctor = new DoctorService(configSvc, _prs, _db, ghDiscovery, probe);
        var report = await doctor.RunAsync();

        var rate = report.Advisories.SingleOrDefault(a => a.Title.StartsWith("Low API rate-limit"));
        rate.Should().NotBeNull();
        rate!.Severity.Should().Be(DoctorAdvisorySeverity.Info);
        rate.Detail.Should().Contain("100/5000");
        // No actions — informational only.
        (rate.Actions == null || rate.Actions.Count == 0).Should().BeTrue();
    }

    [Fact]
    public async Task Rate_Limit_Advisory_Does_Not_Fire_When_Headroom_Is_Healthy()
    {
        // 3000/5000 = 60%, well above the 15% threshold.
        var sources = new[] { OkCheck("gh.com:jenny", "jenny") };
        var configSvc = new StubConfigSvc(BaseReport(sources, allOk: true));
        var ghDiscovery = new StubGhDiscovery(Array.Empty<GitHubAuthIdentity>());
        var probe = new StubRateLimitProbe(
            new RateLimitSnapshot(Remaining: 3000, Limit: 5000,
                ResetAt: DateTimeOffset.UtcNow.AddMinutes(40)));

        var doctor = new DoctorService(configSvc, _prs, _db, ghDiscovery, probe);
        var report = await doctor.RunAsync();

        report.Advisories.Should().NotContain(a => a.Title.StartsWith("Low API rate-limit"));
    }

    [Fact]
    public async Task Rate_Limit_Advisory_Does_Not_Fire_When_Probe_Returns_Null()
    {
        // Probe failure (gh missing, network down, JSON parse error)
        // must not surface a noisy advisory.
        var sources = new[] { OkCheck("gh.com:jenny", "jenny") };
        var configSvc = new StubConfigSvc(BaseReport(sources, allOk: true));
        var ghDiscovery = new StubGhDiscovery(Array.Empty<GitHubAuthIdentity>());
        var probe = new StubRateLimitProbe(snap: null);

        var doctor = new DoctorService(configSvc, _prs, _db, ghDiscovery, probe);
        var report = await doctor.RunAsync();

        report.Advisories.Should().NotContain(a => a.Title.StartsWith("Low API rate-limit"));
    }

    // ---------- helpers ----------

    private static SourceCheck OkCheck(string id, string identity) => new(
        Id: id,
        Kind: SourceConfigKind.GitHub,
        Host: "github.com",
        Enabled: true,
        Ok: true,
        Identity: identity,
        TokenLength: 40,
        Error: null);

    private static DoctorReport BaseReport(IReadOnlyList<SourceCheck> sources, bool allOk) =>
        new(sources, Array.Empty<AdoProjectInfo>(), allOk, "/test/config.json");

    private sealed class StubConfigSvc : IConfigService
    {
        private readonly DoctorReport _report;
        public StubConfigSvc(DoctorReport report) => _report = report;
        public string ConfigPath => _report.ConfigPath;
        public bool ConfigFileExists() => true;
        public Task<DoctorReport> RunDoctorAsync(CancellationToken ct = default) => Task.FromResult(_report);
        public Task<PrInboxConfig> GetAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> AddGitHubSourceAsync(SourceConfigKind kind, string host, string? id = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> AddGitHubSourceWithIdentityAsync(SourceConfigKind kind, string host, string identity, string? id = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> AddAdoProjectAsync(string org, string project, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> RemoveSourceAsync(string sourceId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> RemoveAdoProjectAsync(string org, string project, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetIgnoredReposAsync(IReadOnlyList<string> patterns, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetReviewLauncherFlagsAsync(bool autoSend, bool yolo, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetReviewLauncherTabColorAsync(string tabColor, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetRepoPathFiltersAsync(IReadOnlyDictionary<string, IReadOnlyList<string>> filters, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BindIdentityResult> BindGitHubSourceToIdentityAsync(string sourceId, string identity, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class StubGhDiscovery : IGitHubAuthDiscovery
    {
        private readonly IReadOnlyList<GitHubAuthIdentity> _identities;
        public StubGhDiscovery(IReadOnlyList<GitHubAuthIdentity> identities) => _identities = identities;
        public Task<IReadOnlyList<GitHubAuthIdentity>> ListIdentitiesAsync(string host, CancellationToken ct = default) =>
            Task.FromResult(_identities);
    }

    private sealed class StubRateLimitProbe : IGitHubRateLimitProbe
    {
        private readonly RateLimitSnapshot? _snap;
        public StubRateLimitProbe(RateLimitSnapshot? snap = null) => _snap = snap;
        public Task<RateLimitSnapshot?> GetCoreAsync(string hostname, CancellationToken ct = default)
            => Task.FromResult(_snap);
    }
}
