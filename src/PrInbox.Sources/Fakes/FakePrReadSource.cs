using PrInbox.Core.Models;

namespace PrInbox.Sources.Fakes;

/// <summary>
/// In-memory deterministic fake of <see cref="IPrReadSource"/>. Used by
/// characterization tests to validate the contract independently of any
/// real platform adapter, and by storage tests that need predictable input.
/// </summary>
public sealed class FakePrReadSource : IPrReadSource
{
    private readonly Dictionary<PrIdentity, FakePr> _prs;
    private readonly IReadOnlyList<RemotePullRequest> _authored;

    public FakePrReadSource(
        string sourceId,
        SourceKind kind,
        SourceCapabilities capabilities,
        IReadOnlyList<FakePr> prs,
        IReadOnlyList<RemotePullRequest>? authored = null)
    {
        SourceId = sourceId;
        Kind = kind;
        Capabilities = capabilities;
        _prs = prs.ToDictionary(p => p.PullRequest.Identity);
        _authored = authored ?? Array.Empty<RemotePullRequest>();
    }

    public string SourceId { get; }
    public SourceKind Kind { get; }
    public SourceCapabilities Capabilities { get; }

    public async IAsyncEnumerable<RemotePullRequest> ListAssignedFastAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var pr in _prs.Values.Select(p => p.PullRequest))
        {
            ct.ThrowIfCancellationRequested();
            yield return pr;
        }
        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<RemotePullRequest> ListAuthoredFastAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var pr in _authored)
        {
            ct.ThrowIfCancellationRequested();
            yield return pr;
        }
        await Task.CompletedTask;
    }

    public Task<PrEnrichmentBundle> EnrichAsync(PrIdentity id, CancellationToken ct)
    {
        var pr = GetOrThrow(id);
        return Task.FromResult(new PrEnrichmentBundle(pr.Detail, pr.Threads));
    }

    public Task<IReadOnlyList<RemoteCommit>> GetCommitsAsync(PrIdentity id, CancellationToken ct)
    {
        return Task.FromResult(GetOrThrow(id).Commits);
    }

    public Task<CompareResult> CompareAsync(PrIdentity id, string previousHeadSha, string currentHeadSha, CancellationToken ct)
    {
        var pr = GetOrThrow(id);
        var shaList = pr.Detail.OrderedCommitShas.ToList();

        if (previousHeadSha == currentHeadSha)
        {
            return Task.FromResult(new CompareResult(false, 0, 0));
        }

        var idxCurrent = shaList.IndexOf(currentHeadSha);
        var idxPrevious = shaList.IndexOf(previousHeadSha);
        bool forcePushed = idxPrevious < 0;

        int ahead = forcePushed ? shaList.Count : Math.Max(0, idxPrevious - Math.Max(idxCurrent, 0));
        int behind = forcePushed ? 1 : 0;

        return Task.FromResult(new CompareResult(forcePushed, ahead, behind));
    }

    private FakePr GetOrThrow(PrIdentity id) =>
        _prs.TryGetValue(id, out var pr)
            ? pr
            : throw new KeyNotFoundException($"FakePrReadSource has no PR with identity '{id.Url}'.");
}

/// <summary>
/// A single PR fixture used by <see cref="FakePrReadSource"/>.
/// </summary>
public sealed record FakePr(
    RemotePullRequest PullRequest,
    RemotePullRequestDetail Detail,
    IReadOnlyList<RemoteThread> Threads,
    IReadOnlyList<RemoteCommit> Commits);

/// <summary>
/// Fluent builder for <see cref="FakePrReadSource"/>.
/// </summary>
public sealed class FakePrReadSourceBuilder
{
    private readonly string _sourceId;
    private readonly SourceKind _kind;
    private SourceCapabilities _capabilities;
    private readonly List<FakePr> _prs = new();
    private readonly List<RemotePullRequest> _authored = new();

    public FakePrReadSourceBuilder(string sourceId, SourceKind kind)
    {
        _sourceId = sourceId;
        _kind = kind;
        _capabilities = new SourceCapabilities(
            SupportsGlobalReviewerInbox: kind != SourceKind.AzureDevOps,
            SupportsThreadResolution: true,
            SupportsBotAuthorClassification: kind != SourceKind.AzureDevOps,
            SupportsReviewRequestTimestamps: true,
            SupportsStableRepoIds: true,
            SupportsForcePushDetection: true);
    }

    public FakePrReadSourceBuilder WithCapabilities(SourceCapabilities capabilities)
    {
        _capabilities = capabilities;
        return this;
    }

    public FakePrReadSourceBuilder WithPullRequest(
        RemotePullRequest pr,
        RemotePullRequestDetail detail,
        IReadOnlyList<RemoteThread>? threads = null,
        IReadOnlyList<RemoteCommit>? commits = null)
    {
        _prs.Add(new FakePr(
            pr,
            detail,
            threads ?? Array.Empty<RemoteThread>(),
            commits ?? Array.Empty<RemoteCommit>()));
        return this;
    }

    /// <summary>
    /// Adds a PR to the authored ("My PRs") stream returned by
    /// <see cref="FakePrReadSource.ListAuthoredFastAsync"/>, and flips
    /// <see cref="SourceCapabilities.SupportsAuthoredInbox"/> on so the
    /// orchestrator exercises the authored pass.
    /// </summary>
    public FakePrReadSourceBuilder WithAuthoredPullRequest(RemotePullRequest pr)
    {
        _authored.Add(pr);
        _capabilities = _capabilities with { SupportsAuthoredInbox = true };
        return this;
    }

    public FakePrReadSource Build() => new(_sourceId, _kind, _capabilities, _prs, _authored);
}
