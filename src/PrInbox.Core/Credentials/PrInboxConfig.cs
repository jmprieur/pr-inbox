using System.Text.Json;
using System.Text.Json.Serialization;

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
    /// Default fetches the security-toolkit plugin directly from GitHub
    /// (cached by agency after first use). Examples:
    /// <list type="bullet">
    ///   <item><c>github:1ES-microsoft/ai-plugins:plugins/security-toolkit</c></item>
    ///   <item><c>local:d:/1es/ai-plugins/plugins/security-toolkit</c></item>
    ///   <item><c>ado-git:&lt;org&gt;/&lt;project&gt;/&lt;repo&gt;:&lt;path&gt;</c></item>
    /// </list>
    /// </summary>
    public string Plugin { get; init; } = " market:security-toolkit@1ES-microsoft/ai-plugins";

    /// <summary>Model id passed to <c>agency copilot --model</c>.</summary>
    public string Model { get; init; } = "claude-opus-4.7-xhigh";

    /// <summary>Agent id passed to <c>agency copilot --agent</c>.</summary>
    public string Agent { get; init; } = "security-toolkit:dual-model-review";

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
