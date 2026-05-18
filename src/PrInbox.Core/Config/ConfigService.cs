using PrInbox.Core.Credentials;

namespace PrInbox.Core.Config;

/// <summary>
/// Default <see cref="IConfigService"/> implementation. Operates on the
/// JSON config file at <see cref="PrInboxConfig.DefaultPath"/> (overridable
/// for tests) and, when given a singleton instance, mirrors mutations
/// into that instance's mutable lists so DI-injected consumers see
/// fresh values without a restart.
/// </summary>
public sealed class ConfigService : IConfigService
{
    private readonly PrInboxConfig? _singleton;
    private readonly string _configPath;

    /// <summary>
    /// Constructor for tests / standalone use. <paramref name="configPath"/>
    /// defaults to <see cref="PrInboxConfig.DefaultPath"/>. No singleton
    /// to refresh.
    /// </summary>
    public ConfigService(string? configPath = null)
        : this(singleton: null, configPath)
    {
    }

    /// <summary>
    /// DI constructor. <paramref name="singleton"/> is the
    /// <see cref="PrInboxConfig"/> registered at startup; its mutable
    /// lists are refreshed after every save.
    /// </summary>
    public ConfigService(PrInboxConfig? singleton, string? configPath = null)
    {
        _singleton = singleton;
        _configPath = configPath ?? PrInboxConfig.DefaultPath;
    }

    /// <inheritdoc />
    public string ConfigPath => _configPath;

    /// <inheritdoc />
    public bool ConfigFileExists() => File.Exists(_configPath);

    /// <inheritdoc />
    public Task<PrInboxConfig> GetAsync(CancellationToken ct = default)
        => PrInboxConfig.LoadAsync(_configPath, ct);

    /// <inheritdoc />
    public async Task<bool> AddGitHubSourceAsync(
        SourceConfigKind kind,
        string host,
        string? id = null,
        CancellationToken ct = default)
    {
        if (kind != SourceConfigKind.GitHub && kind != SourceConfigKind.GitHubEnterprise)
        {
            throw new ArgumentException($"AddGitHubSourceAsync only accepts GitHub kinds; got {kind}.", nameof(kind));
        }
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Host is required.", nameof(host));
        }

        var cfg = await PrInboxConfig.LoadAsync(_configPath, ct);
        id ??= DefaultIdFor(kind, host);

        if (cfg.Sources.Any(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        cfg.Sources.Add(new SourceConfig
        {
            Id = id,
            Kind = kind,
            Host = host,
            Identity = "default",
            Enabled = true,
        });

        await SaveAndRefreshAsync(cfg, ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> AddAdoProjectAsync(string org, string project, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(org))     throw new ArgumentException("Org is required.", nameof(org));
        if (string.IsNullOrWhiteSpace(project)) throw new ArgumentException("Project is required.", nameof(project));

        var cfg = await PrInboxConfig.LoadAsync(_configPath, ct);
        if (cfg.Ado.Projects.Any(p =>
                string.Equals(p.Org, org, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.Project, project, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        cfg.Ado.Projects.Add(new AdoProjectConfig { Org = org, Project = project });
        await SaveAndRefreshAsync(cfg, ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveSourceAsync(string sourceId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) return false;

        var cfg = await PrInboxConfig.LoadAsync(_configPath, ct);
        var removed = cfg.Sources.RemoveAll(s =>
            string.Equals(s.Id, sourceId, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return false;

        await SaveAndRefreshAsync(cfg, ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAdoProjectAsync(string org, string project, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(org) || string.IsNullOrWhiteSpace(project)) return false;

        var cfg = await PrInboxConfig.LoadAsync(_configPath, ct);
        var removed = cfg.Ado.Projects.RemoveAll(p =>
            string.Equals(p.Org, org, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.Project, project, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return false;

        await SaveAndRefreshAsync(cfg, ct);
        return true;
    }

    /// <inheritdoc />
    public async Task SetIgnoredReposAsync(IReadOnlyList<string> patterns, CancellationToken ct = default)
    {
        var cfg = await PrInboxConfig.LoadAsync(_configPath, ct);
        cfg.IgnoredRepos.Clear();
        foreach (var p in patterns)
        {
            if (!string.IsNullOrWhiteSpace(p)) cfg.IgnoredRepos.Add(p.Trim());
        }
        await SaveAndRefreshAsync(cfg, ct);
    }

    /// <inheritdoc />
    public async Task SetReviewLauncherFlagsAsync(bool autoSend, bool yolo, CancellationToken ct = default)
    {
        var cfg = await PrInboxConfig.LoadAsync(_configPath, ct);
        cfg.ReviewLauncher.AutoSend = autoSend;
        cfg.ReviewLauncher.Yolo = yolo;
        await SaveAndRefreshAsync(cfg, ct);
    }

    /// <inheritdoc />
    public async Task<DoctorReport> RunDoctorAsync(CancellationToken ct = default)
    {
        var cfg = await PrInboxConfig.LoadAsync(_configPath, ct);
        var rows = new List<SourceCheck>();
        var allOk = true;

        foreach (var sc in cfg.Sources)
        {
            if (!sc.Enabled)
            {
                rows.Add(new SourceCheck(sc.Id, sc.Kind, sc.Host, Enabled: false, Ok: true, Identity: null, TokenLength: null, Error: null));
                continue;
            }

            try
            {
                ITokenProvider provider = sc.Kind switch
                {
                    SourceConfigKind.GitHub or SourceConfigKind.GitHubEnterprise =>
                        new GhCliTokenProvider(sc.Id, sc.Host ?? throw new InvalidOperationException("host required for GitHub source"), sc.Identity),
                    SourceConfigKind.AzureDevOps =>
                        new AzureCliTokenProvider(sc.Id),
                    _ => throw new InvalidOperationException($"Unknown kind {sc.Kind}"),
                };

                var token = await provider.GetTokenAsync(ct);
                var identity = await provider.GetAuthenticatedIdentityAsync(ct);
                rows.Add(new SourceCheck(sc.Id, sc.Kind, sc.Host, sc.Enabled, Ok: true, Identity: identity, TokenLength: token.Length, Error: null));
            }
            catch (TokenAcquisitionException ex)
            {
                allOk = false;
                var firstLine = ex.Message.Split('\n')[0];
                rows.Add(new SourceCheck(sc.Id, sc.Kind, sc.Host, sc.Enabled, Ok: false, Identity: null, TokenLength: null, Error: firstLine));
            }
            catch (Exception ex)
            {
                allOk = false;
                rows.Add(new SourceCheck(sc.Id, sc.Kind, sc.Host, sc.Enabled, Ok: false, Identity: null, TokenLength: null, Error: ex.Message.Split('\n')[0]));
            }
        }

        var adoRows = cfg.Ado.Projects
            .Select(p => new AdoProjectInfo(p.Org, p.Project))
            .ToList();

        return new DoctorReport(rows, adoRows, allOk, _configPath);
    }

    /// <summary>
    /// Save the supplied config to disk and mirror its mutable lists onto
    /// the DI singleton so live consumers (Inbox.razor's IgnoredRepos read,
    /// etc.) see fresh values without a restart.
    /// </summary>
    private async Task SaveAndRefreshAsync(PrInboxConfig cfg, CancellationToken ct)
    {
        await cfg.SaveAsync(_configPath, ct);

        if (_singleton is null) return;

        _singleton.Sources.Clear();
        foreach (var s in cfg.Sources) _singleton.Sources.Add(s);

        _singleton.Ado.Projects.Clear();
        foreach (var p in cfg.Ado.Projects) _singleton.Ado.Projects.Add(p);

        _singleton.IgnoredRepos.Clear();
        foreach (var r in cfg.IgnoredRepos) _singleton.IgnoredRepos.Add(r);

        _singleton.Bots.ExtraLogins.Clear();
        foreach (var b in cfg.Bots.ExtraLogins) _singleton.Bots.ExtraLogins.Add(b);

        // ReviewLauncher: the singleton's ReviewLauncher instance is the
        // same reference ReviewLauncher.SpawnConsole reads each launch,
        // so mutating these two fields in place is what makes Settings
        // changes effective without a restart.
        _singleton.ReviewLauncher.AutoSend = cfg.ReviewLauncher.AutoSend;
        _singleton.ReviewLauncher.Yolo = cfg.ReviewLauncher.Yolo;
    }

    private static string DefaultIdFor(SourceConfigKind kind, string host) => kind switch
    {
        SourceConfigKind.GitHub when string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase) => "gh.com",
        SourceConfigKind.GitHub => $"gh.{host}",
        SourceConfigKind.GitHubEnterprise => $"ghe.{host}",
        _ => host,
    };
}
