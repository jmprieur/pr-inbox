using PrInbox.Core.Credentials;

namespace PrInbox.Sources.AzureDevOps;

/// <summary>
/// A group that appears as a reviewer on active PRs in a project and that
/// the signed-in user is a (transitive) member of.
/// </summary>
public sealed record AdoReviewerGroupCandidate(string Id, string Name, int ActivePrCount);

/// <summary>
/// Finds the reviewer groups worth opting into for an ADO project. ADO has
/// no "groups I'm in that review PRs here" API, and its identity search
/// doesn't resolve group display names reliably, so we sample the most
/// recent active PRs, collect group reviewers, and keep the ones the user
/// belongs to. On-demand only (Settings) — never part of sync.
/// </summary>
public sealed class AdoReviewerGroupDiscovery
{
    /// <summary>Most recent active PRs sampled per discovery run.</summary>
    public const int DefaultSampleSize = 3000;

    private readonly Func<string, AdoApiClient> _clientFactory;

    public AdoReviewerGroupDiscovery(HttpClient? http = null)
        : this(org => new AdoApiClient(org, new AzureCliTokenProvider($"ado:{org}"), http))
    {
    }

    internal AdoReviewerGroupDiscovery(Func<string, AdoApiClient> clientFactory)
    {
        _clientFactory = clientFactory;
    }

    public async Task<IReadOnlyList<AdoReviewerGroupCandidate>> DiscoverAsync(
        string org,
        string project,
        int sampleSize = DefaultSampleSize,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(org);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);

        var client = _clientFactory(org);

        var profile = await client.GetMyProfileAsync(ct);
        if (string.IsNullOrWhiteSpace(profile.Id))
        {
            throw new InvalidOperationException(
                "Azure DevOps profile API returned no user id. Run `az login` against the tenant that owns this org.");
        }

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var pr in client.ListActivePullRequestsAsync(project, sampleSize, ct))
        {
            foreach (var r in pr.Reviewers)
            {
                if (!r.IsContainer || string.IsNullOrWhiteSpace(r.Id)) continue;
                counts[r.Id] = counts.GetValueOrDefault(r.Id) + 1;
                if (!string.IsNullOrWhiteSpace(r.DisplayName)) names.TryAdd(r.Id, r.DisplayName);
            }
        }
        if (counts.Count == 0) return Array.Empty<AdoReviewerGroupCandidate>();

        var me = (await client.GetIdentitiesAsync(new[] { profile.Id }, expandedMembership: true, ct)).FirstOrDefault();
        if (me is null || me.MemberOf.Count == 0) return Array.Empty<AdoReviewerGroupCandidate>();
        var myGroups = new HashSet<string>(me.MemberOf, StringComparer.OrdinalIgnoreCase);

        var groups = await client.GetIdentitiesAsync(counts.Keys.ToList(), expandedMembership: false, ct);
        return groups
            .Where(g => g.Descriptor is not null && myGroups.Contains(g.Descriptor))
            .Select(g => new AdoReviewerGroupCandidate(
                g.Id,
                g.ProviderDisplayName ?? names.GetValueOrDefault(g.Id) ?? g.Id,
                counts.GetValueOrDefault(g.Id)))
            .OrderBy(c => c.ActivePrCount)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
