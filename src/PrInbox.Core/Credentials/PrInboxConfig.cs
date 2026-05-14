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
