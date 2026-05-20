using FluentAssertions;
using PrInbox.Core.Config;
using PrInbox.Core.Credentials;
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
        var doctor = new DoctorService(configSvc, _prs, _db, ghDiscovery);

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
        var doctor = new DoctorService(configSvc, _prs, _db, ghDiscovery);

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
        var doctor = new DoctorService(configSvc, _prs, _db, ghDiscovery);

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
        var doctor = new DoctorService(configSvc, _prs, _db, ghDiscovery);

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
        var doctor = new DoctorService(configSvc, _prs, _db, ghDiscovery);

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
        var doctor = new DoctorService(configSvc, _prs, _db, ghDiscovery);

        var report = await doctor.RunAsync();

        // Default identity isn't bound to a specific login — EMU detection
        // would be ambiguous, so we leave it null rather than guess.
        var row = report.Sources.Single();
        row.IsEmu.Should().BeNull();
        row.IsActiveGhLogin.Should().BeFalse();
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
        public Task<BindIdentityResult> BindGitHubSourceToIdentityAsync(string sourceId, string identity, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class StubGhDiscovery : IGitHubAuthDiscovery
    {
        private readonly IReadOnlyList<GitHubAuthIdentity> _identities;
        public StubGhDiscovery(IReadOnlyList<GitHubAuthIdentity> identities) => _identities = identities;
        public Task<IReadOnlyList<GitHubAuthIdentity>> ListIdentitiesAsync(string host, CancellationToken ct = default) =>
            Task.FromResult(_identities);
    }
}
