using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PrInbox.Core.Credentials;

/// <summary>
/// On-disk pr-inbox config. Contains source definitions, ADO project list,
/// and bot login overrides. <b>Never contains tokens.</b>
/// </summary>
public sealed class PrInboxConfig
{
    public int SchemaVersion { get; init; } = 1;

    public List<SourceConfig> Sources { get; init; } = new();

    public AdoConfig Ado { get; init; } = new();

    public BotConfig Bots { get; init; } = new();

    /// <summary>
    /// Defaults for the in-app Review launcher (which spawns
    /// <c>agency copilot</c> in a new Windows Terminal tab). Each field
    /// has a sensible built-in default; absent / null fields fall
    /// through to those defaults.
    /// </summary>
    public ReviewLauncherSettings ReviewLauncher { get; init; } = new();

    /// <summary>
    /// Regex patterns. Any PR whose display repo (e.g.
    /// <c>1ES/Spmi</c>) fully matches one of these is hidden from the
    /// inbox by default. Toggle "Show ignored" in the UI to reveal.
    /// Data is still synced; this is a UI-level filter.
    /// </summary>
    public List<string> IgnoredRepos { get; init; } = new();

    /// <summary>
    /// Returns the path used by <see cref="LoadOrCreateAsync"/> when no
    /// override is supplied. Honors the optional environment variable
    /// <c>PR_INBOX_CONFIG_PATH</c> for tests.
    /// </summary>
    public static string DefaultPath
    {
        get
        {
            var fromEnv = Environment.GetEnvironmentVariable("PR_INBOX_CONFIG_PATH");
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                return fromEnv;
            }
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "PrInbox", "config.json");
        }
    }

    /// <summary>
    /// Load from <paramref name="path"/>; if the file does not exist, return
    /// an empty default config without creating the file.
    /// </summary>
    public static async Task<PrInboxConfig> LoadAsync(string? path = null, CancellationToken ct = default)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
        {
            return new PrInboxConfig();
        }
        await using var stream = File.OpenRead(path);
        var config = await JsonSerializer.DeserializeAsync<PrInboxConfig>(stream, SerializerOptions, ct);
        return config ?? new PrInboxConfig();
    }

    /// <summary>
    /// Save to <paramref name="path"/>, creating the parent directory if needed.
    /// </summary>
    public async Task SaveAsync(string? path = null, CancellationToken ct = default)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, this, SerializerOptions, ct);
    }

    internal static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };
}

/// <summary>
/// One source configured in pr-inbox.
/// </summary>
public sealed class SourceConfig
{
    /// <summary>Stable id, e.g. <c>gh.com</c>, <c>ghe.contoso.com</c>, <c>ado:mseng</c>.</summary>
    public required string Id { get; init; }

    /// <summary>The kind of platform.</summary>
    public required SourceConfigKind Kind { get; init; }

    /// <summary>
    /// Host for GitHub-flavored sources (e.g. <c>github.com</c>,
    /// <c>github.contoso.com</c>). Null for ADO.
    /// </summary>
    public string? Host { get; init; }

    /// <summary>
    /// Friendly name for the identity used to query this source.
    /// Defaults to <c>default</c>; future multi-identity-per-source can extend.
    /// </summary>
    public string Identity { get; init; } = "default";

    /// <summary>
    /// Whether this source is enabled. Disabled sources are skipped by
    /// <c>sync</c> and hidden from <c>list</c> defaults.
    /// </summary>
    public bool Enabled { get; init; } = true;
}

public enum SourceConfigKind
{
    GitHub,
    GitHubEnterprise,
    AzureDevOps,
}

public sealed class AdoConfig
{
    public List<AdoProjectConfig> Projects { get; init; } = new();
}

public sealed class AdoProjectConfig
{
    public required string Org { get; init; }
    public required string Project { get; init; }
}

/// <summary>
/// Configuration for bot detection. <see cref="ExtraLogins"/> is merged with
/// the hardcoded default list.
/// </summary>
public sealed class BotConfig
{
    public List<string> ExtraLogins { get; init; } = new();
}

/// <summary>
/// Defaults the Web UI uses when spawning <c>agency copilot</c> for a
/// review. Each field is overridable; falls through to the matching
/// <c>PRINBOX_REVIEW_*</c> environment variable if the config value is
/// empty.
/// </summary>
public sealed class ReviewLauncherSettings
{
    /// <summary>
    /// Plugin spec passed to <c>agency copilot --plugin &lt;...&gt;</c>.
    /// Defaults to the <c>dual-review</c> plugin published from this
    /// repo's marketplace (<c>.github/plugin/marketplace.json</c>). For
    /// local development against an unpublished working tree, use a
    /// <c>local:</c> spec. Examples:
    /// <list type="bullet">
    ///   <item><c>market:dual-review@jmprieur/pr-inbox</c></item>
    ///   <item><c>github:jmprieur/pr-inbox:plugins/dual-review</c></item>
    ///   <item><c>local:d:/1es/pr-inbox/plugins/dual-review</c></item>
    ///   <item><c>ado-git:&lt;org&gt;/&lt;project&gt;/&lt;repo&gt;:&lt;path&gt;</c></item>
    /// </list>
    /// </summary>
    public string Plugin { get; init; } = "market:dual-review@jmprieur/pr-inbox";

    /// <summary>Model id passed to <c>agency copilot --model</c>.</summary>
    public string Model { get; init; } = "claude-opus-4.8";

    /// <summary>Agent id passed to <c>agency copilot --agent</c>.</summary>
    public string Agent { get; init; } = "dual-review:dual-model-review";

    /// <summary>
    /// Hex colour applied to the Windows Terminal tab spawned for a
    /// review (<c>wt nt --tabColor</c>), so every review window is
    /// visually distinct from ordinary terminals. Accepts <c>#rgb</c> or
    /// <c>#rrggbb</c>. Defaults to the app accent so all review tabs
    /// share one colour. Set to empty to disable colouring. Ignored by
    /// the non-<c>wt</c> fallback launcher (plain pwsh has no tab colour).
    /// </summary>
    /// <remarks>
    /// Mutable (<c>set</c> not <c>init</c>) so the Settings page can
    /// update it on the live DI singleton and the next review launch
    /// picks it up without a process restart.
    /// </remarks>
    public string TabColor { get; set; } = "#5da4ff";

    /// <summary>
    /// Returns <paramref name="value"/> trimmed if it is a Windows
    /// Terminal–acceptable hex colour (<c>#rgb</c> or <c>#rrggbb</c>);
    /// otherwise <c>null</c>. The single source of truth shared by the
    /// launcher (which drops invalid values rather than letting <c>wt</c>
    /// reject the whole command line) and the Settings page (which uses
    /// it to validate user input). Empty / whitespace returns
    /// <c>null</c> — i.e. "no tab colour".
    /// </summary>
    public static string? NormalizeTabColor(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        return TabColorPattern.IsMatch(v) ? v : null;
    }

    private static readonly Regex TabColorPattern =
        new("^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$", RegexOptions.Compiled);

    /// <summary>MCP servers to enable (each becomes one <c>--mcp</c> flag).</summary>
    public List<string> AdditionalMcps { get; init; } = new() { "workiq", "teams" };

    /// <summary>
    /// When true (default), the launcher sends a short bootstrap prompt
    /// to copilot via <c>-i "Read brief.md and proceed."</c> so the
    /// session starts interactively AND auto-executes immediately — no
    /// Ctrl+V paste. When false, falls back to the legacy "copy brief
    /// to clipboard, user pastes manually" flow.
    /// </summary>
    /// <remarks>
    /// Mutable (<c>set</c> not <c>init</c>) so the Settings page can
    /// toggle it on the live DI singleton and the next review launch
    /// picks it up without a process restart.
    /// </remarks>
    public bool AutoSend { get; set; } = true;

    /// <summary>
    /// When true, appends <c>--yolo</c> to copilot's pass-through args
    /// (equivalent to <c>--allow-all-tools --allow-all-paths
    /// --allow-all-urls</c>) — every permission prompt is auto-approved
    /// for the session. Default <c>false</c>: opt in per environment if
    /// you want the unattended experience.
    /// </summary>
    /// <remarks>
    /// Mutable (<c>set</c> not <c>init</c>) so the Settings page can
    /// toggle it on the live DI singleton and the next review launch
    /// picks it up without a process restart.
    /// </remarks>
    public bool Yolo { get; set; } = false;
}
