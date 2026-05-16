using Microsoft.Extensions.Logging;
using PrInbox.Core.Credentials;

namespace PrInbox.Publishers;

/// <summary>
/// Default selector. Constructed once at app start: builds one publisher
/// per source binding (host + identity) and dispatches by URL host
/// (URL-only path) or URL host + identity (orchestrator path).
/// </summary>
/// <remarks>
/// For hosts with a single identity, both lookup paths return the same
/// publisher. <c>github.com</c> can host two identities (jmprieur,
/// jmprieur_microsoft); a default identity per host is used when the
/// caller doesn't specify one.
/// </remarks>
public sealed class ConfigDrivenPublisherSelector : IPublisherSelector
{
    private readonly IReadOnlyDictionary<(string Host, string Identity), IPrReviewPublisher> _byPair;
    private readonly IReadOnlyDictionary<string, string> _defaultIdentityByHost;
    private readonly ILogger<ConfigDrivenPublisherSelector> _log;

    /// <param name="publishersByPair">
    /// Map of (lowercase host, identity) → publisher. Identity is the
    /// PR row's <c>identity_used</c> value.
    /// </param>
    /// <param name="defaultIdentityByHost">
    /// Map of host → preferred identity. Used by <see cref="Select"/> when
    /// the caller only has the URL. For github.com prefer EMU
    /// (jmprieur_microsoft); the web wiring chooses these defaults.
    /// </param>
    public ConfigDrivenPublisherSelector(
        IReadOnlyDictionary<(string Host, string Identity), IPrReviewPublisher> publishersByPair,
        IReadOnlyDictionary<string, string> defaultIdentityByHost,
        ILogger<ConfigDrivenPublisherSelector> log)
    {
        _byPair = publishersByPair;
        _defaultIdentityByHost = defaultIdentityByHost;
        _log = log;
    }

    public IPrReviewPublisher Select(string prUrl)
    {
        var target = PullRequestUrlParser.Parse(prUrl);
        var host = target.Host.ToLowerInvariant();
        if (!_defaultIdentityByHost.TryGetValue(host, out var defaultIdentity))
        {
            throw new InvalidOperationException(
                $"No publisher configured for host '{target.Host}'. Registered hosts: {string.Join(", ", _defaultIdentityByHost.Keys)}.");
        }
        return SelectFor(prUrl, defaultIdentity);
    }

    public IPrReviewPublisher SelectFor(string prUrl, string identityUsed)
    {
        var target = PullRequestUrlParser.Parse(prUrl);
        var host = target.Host.ToLowerInvariant();
        if (_byPair.TryGetValue((host, identityUsed), out var pub))
        {
            return pub;
        }
        // Fallback: any publisher for that host.
        var any = _byPair.FirstOrDefault(kv => kv.Key.Host == host).Value;
        if (any is not null) return any;
        throw new InvalidOperationException(
            $"No publisher configured for ({target.Host}, {identityUsed}). Registered pairs: {string.Join(", ", _byPair.Keys.Select(k => $"({k.Host},{k.Identity})"))}.");
    }

    public string? IdentityForLogging(string prUrl)
    {
        try
        {
            var target = PullRequestUrlParser.Parse(prUrl);
            var host = target.Host.ToLowerInvariant();
            return _defaultIdentityByHost.TryGetValue(host, out var id) ? id : null;
        }
        catch
        {
            return null;
        }
    }
}
