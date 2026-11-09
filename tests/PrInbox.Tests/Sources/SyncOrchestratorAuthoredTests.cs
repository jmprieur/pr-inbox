using PrInbox.Core.Models;
using PrInbox.Core.Storage;
using PrInbox.Sources;
using PrInbox.Sources.Fakes;

namespace PrInbox.Tests.Sources;

/// <summary>
/// Behavioral tests for the authored ("My PRs") pass added to
/// <see cref="SyncOrchestrator.RunFastAsync"/>: role tagging
/// (<see cref="MyRole"/>), the <c>not_reviewer</c> tracking sentinel, the
/// reviewer/authored view separation, and the role-scoped reviewer reconcile.
/// </summary>
public class SyncOrchestratorAuthoredTests : IAsyncLifetime
{
    private string _connString = string.Empty;
    private PrInboxDb _db = null!;
    private Microsoft.Data.Sqlite.SqliteConnection _keepAlive = null!;

    private PullRequestRepository _prs = null!;
    private PrSnapshotRepository _snaps = null!;
    private ObservedThreadRepository _threads = null!;
    private SyncRunRepository _syncRuns = null!;

    public async Task InitializeAsync()
    {
        _connString = PrInboxDb.InMemoryConnectionString($"orch-authored-{Guid.NewGuid():N}");
        _db = new PrInboxDb(_connString);
        _keepAlive = await _db.OpenAsync();
        await new MigrationRunner().MigrateAsync(_connString);

        _prs = new PullRequestRepository(_db);
        _snaps = new PrSnapshotRepository(_db);
        _threads = new ObservedThreadRepository(_db);
        _syncRuns = new SyncRunRepository(_db);
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    [Fact]
    public async Task RunFast_Fetches_Once_Per_Author_Across_Both_Passes_And_Refetches_Next_Sync()
    {
        var ids = Enumerable.Range(1, 4)
            .Select(n => new PrIdentity($"https://github.com/owner/repo/pull/{n}", $"gh.com:100#{n}"))
            .ToArray();
        var source = new ProfileSource(new FakePrReadSourceBuilder("gh.com:emu", SourceKind.GitHub)
            .WithPullRequest(BasicPr(ids[0]), Detail(ids[0]))
            .WithPullRequest(BasicPr(ids[1]) with { AuthorLogin = "OCTOCAT" }, Detail(ids[1]))
            .WithAuthoredPullRequest(BasicPr(ids[2]))
            .WithAuthoredPullRequest(BasicPr(ids[3]) with { AuthorLogin = "alice" })
            .Build());
        var orch = new SyncOrchestrator(source, _prs, _snaps, _threads, _syncRuns);

        (await orch.RunFastAsync("self", null, CancellationToken.None)).Status.Should().Be(SyncRunStatus.Ok);
        source.RequestedLogins.Should().Equal("octocat", "alice");
        foreach (var id in ids)
            (await _prs.GetAsync(id.Url, CancellationToken.None))!.AuthorDisplayName.Should().Be("First Name");

        source.Name = "Updated Name";
        await orch.RunFastAsync("self", null, CancellationToken.None);
        source.RequestedLogins.Should().Equal("octocat", "alice", "octocat", "alice");
        foreach (var id in ids)
            (await _prs.GetAsync(id.Url, CancellationToken.None))!.AuthorDisplayName.Should().Be("Updated Name");

        source.Name = null;
        await orch.RunFastAsync("self", null, CancellationToken.None);
        source.RequestedLogins.Should().HaveCount(6);
        foreach (var id in ids)
            (await _prs.GetAsync(id.Url, CancellationToken.None))!.AuthorDisplayName.Should().BeNull();
    }

    [Theory]
    [InlineData(false, "octocat", 0)]
    [InlineData(true, "", 0)]
    [InlineData(true, "octocat", 1)]
    public async Task RunFast_Profile_Flag_And_Missing_Login_Control_Requests(bool enabled, string login, int count)
    {
        var id = new PrIdentity("https://github.com/owner/repo/pull/1", "gh.com:100#1");
        var pr = BasicPr(id) with { AuthorLogin = login };
        var source = new ProfileSource(new FakePrReadSourceBuilder("gh.com:emu", SourceKind.GitHub)
            .WithPullRequest(pr, Detail(id))
            .WithAuthoredPullRequest(pr)
            .Build()) { RetrieveAuthorDisplayNames = enabled, Name = null };

        var result = await new SyncOrchestrator(source, _prs, _snaps, _threads, _syncRuns)
            .RunFastAsync("self", null, CancellationToken.None);

        result.Status.Should().Be(SyncRunStatus.Ok);
        source.RequestedLogins.Should().HaveCount(count);
        (await _prs.GetAsync(id.Url, CancellationToken.None))!.AuthorLogin.Should().Be(login);
        (await _prs.GetAsync(id.Url, CancellationToken.None))!.AuthorDisplayName.Should().BeNull();
    }

    [Fact]
    public async Task RunFast_Profile_Error_Fails_Sync_And_Preserves_Reviewer_Tracking()
    {
        var id = new PrIdentity("https://github.com/owner/repo/pull/1", "gh.com:100#1");
        var missingId = new PrIdentity("https://github.com/owner/repo/pull/2", "gh.com:100#2");
        var fake = new FakePrReadSourceBuilder("gh.com:emu", SourceKind.GitHub)
            .WithPullRequest(BasicPr(id), Detail(id)).Build();
        var initial = new FakePrReadSourceBuilder("gh.com:emu", SourceKind.GitHub)
            .WithPullRequest(BasicPr(id), Detail(id))
            .WithPullRequest(BasicPr(missingId), Detail(missingId)).Build();
        await new SyncOrchestrator(initial, _prs, _snaps, _threads, _syncRuns)
            .RunFastAsync("self", null, CancellationToken.None);
        var source = new ProfileSource(fake) { Error = new HttpRequestException("profile request failed") };

        var result = await new SyncOrchestrator(source, _prs, _snaps, _threads, _syncRuns)
            .RunFastAsync("self", null, CancellationToken.None);

        result.Status.Should().Be(SyncRunStatus.Failed);
        result.Error.Should().Contain("profile request failed");
        source.RequestedLogins.Should().ContainSingle();
        (await _prs.GetAsync(id.Url, CancellationToken.None))!.TrackingReason.Should().Be(TrackingReason.Assigned);
        (await _prs.GetAsync(missingId.Url, CancellationToken.None))!.TrackingReason.Should().Be(TrackingReason.Assigned);
    }

    [Fact]
    public async Task RunFast_Profile_Cancellation_Is_Propagated()
    {
        var id = new PrIdentity("https://github.com/owner/repo/pull/1", "gh.com:100#1");
        var source = new ProfileSource(new FakePrReadSourceBuilder("gh.com:emu", SourceKind.GitHub)
            .WithPullRequest(BasicPr(id), Detail(id)).Build())
        {
            Error = new OperationCanceledException(),
        };
        var orch = new SyncOrchestrator(source, _prs, _snaps, _threads, _syncRuns);
        Func<Task> sync = () => orch.RunFastAsync("self", null, CancellationToken.None);
        await sync.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RunFast_Persists_AuthorDisplayName_For_Both_Passes()
    {
        var reviewerId = new PrIdentity("https://dev.azure.com/fabrikam/Context/_git/MyRepo/pullrequest/1", "ado:1#1");
        var authoredId = new PrIdentity("https://dev.azure.com/fabrikam/Context/_git/MyRepo/pullrequest/2", "ado:1#2");
        var reviewerPr = BasicPr(reviewerId) with
        {
            SourceKind = SourceKind.AzureDevOps,
            SourceId = "ado:fabrikam/Context",
            AuthorLogin = "alice@example.com",
            AuthorDisplayName = "Alice Smith",
        };
        var authoredPr = reviewerPr with { Identity = authoredId, Url = authoredId.Url, Number = 2 };
        var source = new FakePrReadSourceBuilder("ado:fabrikam/Context", SourceKind.AzureDevOps)
            .WithPullRequest(reviewerPr, Detail(reviewerId))
            .WithAuthoredPullRequest(authoredPr)
            .Build();

        await new SyncOrchestrator(source, _prs, _snaps, _threads, _syncRuns)
            .RunFastAsync("self", progress: null, CancellationToken.None);

        foreach (var id in new[] { reviewerId, authoredId })
        {
            var row = await _prs.GetAsync(id.Url, CancellationToken.None);
            row!.AuthorLogin.Should().Be("alice@example.com");
            row.AuthorDisplayName.Should().Be("Alice Smith");
        }
    }

    [Fact]
    public async Task RunFast_TagsAuthoredPrs_AsAuthor_NotReviewer_AndSeparatesViews()
    {
        var reviewerId = new PrIdentity("https://github.com/owner/repo/pull/1", "gh.com:100#1000");
        var authoredId = new PrIdentity("https://github.com/owner/repo/pull/9", "gh.com:100#9000");

        var source = new FakePrReadSourceBuilder("gh.com:emu", SourceKind.GitHub)
            .WithPullRequest(BasicPr(reviewerId), Detail(reviewerId))
            .WithAuthoredPullRequest(BasicPr(authoredId))
            .Build();
        var orch = new SyncOrchestrator(source, _prs, _snaps, _threads, _syncRuns);

        await orch.RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        var reviewer = await _prs.GetAsync(reviewerId.Url, CancellationToken.None);
        reviewer!.MyRole.Should().Be(MyRole.Reviewer);
        reviewer.TrackingReason.Should().Be(TrackingReason.Assigned);

        var authored = await _prs.GetAsync(authoredId.Url, CancellationToken.None);
        authored!.MyRole.Should().Be(MyRole.Author);
        authored.TrackingReason.Should().Be(TrackingReason.NotReviewer);

        // The authored view shows only authored PRs...
        var authoredList = await _prs.ListAuthoredAsync(CancellationToken.None);
        authoredList.Select(r => r.Url).Should().BeEquivalentTo(new[] { authoredId.Url });

        // ...and the reviewer inbox (ListActive) excludes author-only rows.
        var reviewerList = await _prs.ListActiveAsync(CancellationToken.None);
        reviewerList.Select(r => r.Url).Should().Contain(reviewerId.Url);
        reviewerList.Select(r => r.Url).Should().NotContain(authoredId.Url);
    }

    [Fact]
    public async Task RunFast_PrSeenInBothPasses_BecomesBoth_AndAppearsInBothViews()
    {
        var id = new PrIdentity("https://github.com/owner/repo/pull/3", "gh.com:100#3000");
        var pr = BasicPr(id);

        var source = new FakePrReadSourceBuilder("gh.com:emu", SourceKind.GitHub)
            .WithPullRequest(pr, Detail(id))   // reviewer pass
            .WithAuthoredPullRequest(pr)        // authored pass (same identity)
            .Build();
        var orch = new SyncOrchestrator(source, _prs, _snaps, _threads, _syncRuns);

        await orch.RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        var row = await _prs.GetAsync(id.Url, CancellationToken.None);
        row!.MyRole.Should().Be(MyRole.Both);
        // Reviewer lifecycle still applies for a Both PR.
        row.TrackingReason.Should().Be(TrackingReason.Assigned);

        (await _prs.ListActiveAsync(CancellationToken.None)).Select(r => r.Url).Should().Contain(id.Url);
        (await _prs.ListAuthoredAsync(CancellationToken.None)).Select(r => r.Url).Should().Contain(id.Url);
    }

    [Fact]
    public async Task ReviewerReconcile_DemotesDisappearedReviewer_ButLeavesAuthorOnlyRows()
    {
        var reviewerId = new PrIdentity("https://github.com/owner/repo/pull/1", "gh.com:100#1000");
        var authoredId = new PrIdentity("https://github.com/owner/repo/pull/9", "gh.com:100#9000");

        // T0: a reviewer PR and an authored PR.
        var t0 = new FakePrReadSourceBuilder("gh.com:emu", SourceKind.GitHub)
            .WithPullRequest(BasicPr(reviewerId), Detail(reviewerId))
            .WithAuthoredPullRequest(BasicPr(authoredId))
            .Build();
        await new SyncOrchestrator(t0, _prs, _snaps, _threads, _syncRuns)
            .RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        // T1: the reviewer PR is gone from the reviewer query; the authored PR remains.
        var t1 = new FakePrReadSourceBuilder("gh.com:emu", SourceKind.GitHub)
            .WithAuthoredPullRequest(BasicPr(authoredId))
            .Build();
        await new SyncOrchestrator(t1, _prs, _snaps, _threads, _syncRuns)
            .RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        // The vanished reviewer PR is demoted by the reviewer reconcile.
        var reviewer = await _prs.GetAsync(reviewerId.Url, CancellationToken.None);
        reviewer!.TrackingReason.Should().Be(TrackingReason.PreviouslyAssigned);

        // The author-only PR is untouched by the reviewer reconcile.
        var authored = await _prs.GetAsync(authoredId.Url, CancellationToken.None);
        authored!.MyRole.Should().Be(MyRole.Author);
        authored.TrackingReason.Should().Be(TrackingReason.NotReviewer);
    }

    [Fact]
    public async Task AuthoredRows_AreIncluded_InEnrichmentCandidates()
    {
        var authoredId = new PrIdentity("https://github.com/owner/repo/pull/9", "gh.com:100#9000");

        var source = new FakePrReadSourceBuilder("gh.com:emu", SourceKind.GitHub)
            .WithAuthoredPullRequest(BasicPr(authoredId))
            .Build();
        await new SyncOrchestrator(source, _prs, _snaps, _threads, _syncRuns)
            .RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        // Author-only rows (tracking_reason = not_reviewer) must still be
        // enrichment candidates, so CI / mergeable / review-decision get fetched.
        var candidates = await _prs.ListNeedingEnrichmentAsync(
            "gh.com:emu", "jmprieur_microsoft", minDossierVersion: 0, CancellationToken.None);
        candidates.Select(r => r.Url).Should().Contain(authoredId.Url);
    }

    [Fact]
    public async Task ListAuthored_ExcludesMergedAndClosed_AuthoredPrs()
    {
        var openId = new PrIdentity("https://github.com/owner/repo/pull/9", "gh.com:100#9000");
        var mergedId = new PrIdentity("https://github.com/owner/repo/pull/8", "gh.com:100#8000");

        var source = new FakePrReadSourceBuilder("gh.com:emu", SourceKind.GitHub)
            .WithAuthoredPullRequest(BasicPr(openId))
            .WithAuthoredPullRequest(BasicPr(mergedId))
            .Build();
        await new SyncOrchestrator(source, _prs, _snaps, _threads, _syncRuns)
            .RunFastAsync("jmprieur_microsoft", progress: null, CancellationToken.None);

        // One authored PR merges.
        await _prs.UpdateStatusAsync(mergedId.Url, PullRequestStatus.Merged, CancellationToken.None);

        // The canonical authored list (and the default "My PRs" view, which
        // mirrors it) shows open PRs only; merged/closed are hidden.
        var authored = await _prs.ListAuthoredAsync(CancellationToken.None);
        authored.Select(r => r.Url).Should().Contain(openId.Url);
        authored.Select(r => r.Url).Should().NotContain(mergedId.Url);
    }

    private sealed class ProfileSource(IPrReadSource inner) : IPrReadSource
    {
        public string SourceId => inner.SourceId;
        public SourceKind Kind => inner.Kind;
        public SourceCapabilities Capabilities => inner.Capabilities;
        public bool RetrieveAuthorDisplayNames { get; init; } = true;
        public string? Name { get; set; } = "First Name";
        public Exception? Error { get; init; }
        public List<string> RequestedLogins { get; } = new();

        public Task<string?> FetchAuthorDisplayNameAsync(string login, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            RequestedLogins.Add(login);
            return Error is null ? Task.FromResult(Name) : Task.FromException<string?>(Error);
        }

        public IAsyncEnumerable<RemotePullRequest> ListAssignedFastAsync(CancellationToken ct) => inner.ListAssignedFastAsync(ct);
        public IAsyncEnumerable<RemotePullRequest> ListAuthoredFastAsync(CancellationToken ct) => inner.ListAuthoredFastAsync(ct);
        public Task<PrEnrichmentBundle> EnrichAsync(PrIdentity id, CancellationToken ct) => inner.EnrichAsync(id, ct);
        public Task<IReadOnlyList<RemoteCommit>> GetCommitsAsync(PrIdentity id, CancellationToken ct) => inner.GetCommitsAsync(id, ct);
        public Task<CompareResult> CompareAsync(PrIdentity id, string previousHeadSha, string currentHeadSha, CancellationToken ct) =>
            inner.CompareAsync(id, previousHeadSha, currentHeadSha, ct);
    }

    private static RemotePullRequest BasicPr(PrIdentity id) =>
        new(
            Identity: id,
            SourceKind: SourceKind.GitHub,
            SourceId: "gh.com:emu",
            DisplayRepo: "owner/repo",
            Number: int.Parse(id.Url[(id.Url.LastIndexOf('/') + 1)..]),
            Title: "Sample",
            AuthorLogin: "octocat",
            Url: id.Url,
            Status: PullRequestStatus.Open,
            LastUpdated: DateTimeOffset.Parse("2026-05-13T10:00:00Z"));

    private static RemotePullRequestDetail Detail(PrIdentity id) =>
        new(
            Identity: id,
            HeadSha: "abc1234567890aaa",
            BaseSha: "base000000000000",
            MergeBaseSha: null,
            OrderedCommitShas: new[] { "abc1234567890aaa" },
            ReviewerState: ReviewerState.Requested,
            Status: PullRequestStatus.Open,
            RawMetadataJson: "{}");
}
