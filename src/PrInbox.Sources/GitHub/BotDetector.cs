using PrInbox.Core.Models;

namespace PrInbox.Sources.GitHub;

/// <summary>
/// Classifies a thread or comment author as bot or human, and tags known
/// Copilot variants for routing in the review brief.
/// </summary>
/// <remarks>
/// <para>
/// Detection cascade:
/// </para>
/// <list type="number">
///   <item>GitHub's <c>user.type == "Bot"</c> is the primary signal — trust it for IsBot.</item>
///   <item>Login matches one of the well-known Copilot patterns → <c>BotKind.CopilotReview</c> / <c>CopilotCodingAgent</c>.</item>
///   <item>Login matches <c>github-actions[bot]</c> → <c>BotKind.GitHubActions</c>.</item>
///   <item>Login matches <c>dependabot[bot]</c> → <c>BotKind.Dependabot</c>.</item>
///   <item>Login appears in the user-configured <see cref="_extraBotLogins"/> set → <c>BotKind.Other</c>.</item>
///   <item>Any other <c>*[bot]</c> login → <c>BotKind.Other</c>.</item>
///   <item>Otherwise → not a bot.</item>
/// </list>
/// </remarks>
public sealed class BotDetector
{
    private static readonly HashSet<string> KnownCopilotReviewLogins = new(StringComparer.OrdinalIgnoreCase)
    {
        "copilot-pull-request-reviewer[bot]",
        "copilot[bot]",
        "Copilot",
    };

    private static readonly HashSet<string> KnownCopilotCodingAgentLogins = new(StringComparer.OrdinalIgnoreCase)
    {
        "copilot-coding-agent[bot]",
    };

    private readonly HashSet<string> _extraBotLogins;

    public BotDetector(IEnumerable<string>? extraBotLogins = null)
    {
        _extraBotLogins = new HashSet<string>(
            extraBotLogins ?? Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Classify the given author. <paramref name="reportedTypeIsBot"/> is the
    /// boolean form of GitHub's <c>user.type == "Bot"</c> flag.
    /// </summary>
    public (bool IsBot, BotKind? Kind) Classify(string? login, bool reportedTypeIsBot)
    {
        if (string.IsNullOrWhiteSpace(login))
        {
            return (reportedTypeIsBot, reportedTypeIsBot ? BotKind.Other : null);
        }

        if (KnownCopilotReviewLogins.Contains(login))
        {
            return (true, BotKind.CopilotReview);
        }
        if (KnownCopilotCodingAgentLogins.Contains(login))
        {
            return (true, BotKind.CopilotCodingAgent);
        }
        if (string.Equals(login, "github-actions[bot]", StringComparison.OrdinalIgnoreCase))
        {
            return (true, BotKind.GitHubActions);
        }
        if (string.Equals(login, "dependabot[bot]", StringComparison.OrdinalIgnoreCase))
        {
            return (true, BotKind.Dependabot);
        }
        if (_extraBotLogins.Contains(login))
        {
            return (true, BotKind.Other);
        }
        if (reportedTypeIsBot || login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase))
        {
            return (true, BotKind.Other);
        }
        return (false, null);
    }
}
