using System.Text.Json;

namespace PrInbox.Core.Credentials;

/// <summary>
/// A lean, importable subset of <see cref="PrInboxConfig"/> that a profile file
/// (e.g. <c>profiles/microsoft.json</c>) can set — currently the identity
/// taxonomy and the review launch command. Keeping it lean means a profile can
/// never override sources, tokens, or other sensitive state. Applied via
/// <c>pr-inbox config import &lt;file&gt;</c>.
/// </summary>
public sealed class ConfigProfile
{
    /// <summary>Replaces <see cref="PrInboxConfig.IdentityClasses"/> when set.</summary>
    public List<IdentityClass>? IdentityClasses { get; init; }

    /// <summary>Review-launcher overrides.</summary>
    public ReviewLauncherProfile? ReviewLauncher { get; init; }

    /// <summary>Web (camelCase) JSON options for reading profile files; comments allowed.</summary>
    public static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip };

    /// <summary>
    /// Overlays the profile's set fields onto <paramref name="config"/> in place.
    /// Returns a human-readable list of what changed (empty when nothing was set).
    /// </summary>
    public IReadOnlyList<string> ApplyTo(PrInboxConfig config)
    {
        var changes = new List<string>();
        if (IdentityClasses is { Count: > 0 })
        {
            config.IdentityClasses.Clear();
            config.IdentityClasses.AddRange(IdentityClasses);
            changes.Add($"{IdentityClasses.Count} identity class(es)");
        }
        if (!string.IsNullOrWhiteSpace(ReviewLauncher?.LaunchCommand))
        {
            config.ReviewLauncher.LaunchCommand = ReviewLauncher!.LaunchCommand!.Trim();
            changes.Add("review launch command");
        }
        if (!string.IsNullOrWhiteSpace(ReviewLauncher?.Model))
        {
            config.ReviewLauncher.Model = ReviewLauncher!.Model!.Trim();
            changes.Add("review model");
        }
        return changes;
    }
}

/// <summary>Review-launcher fields a <see cref="ConfigProfile"/> may set.</summary>
public sealed class ReviewLauncherProfile
{
    /// <summary>Overrides <see cref="ReviewLauncherSettings.LaunchCommand"/> when set.</summary>
    public string? LaunchCommand { get; init; }

    /// <summary>Overrides <see cref="ReviewLauncherSettings.Model"/> when set.</summary>
    public string? Model { get; init; }
}
