using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using PrInbox.Core.Credentials;
using PrInbox.Core.Reviewing;
using PrInbox.Web.Services;

namespace PrInbox.Tests.Web;

public sealed class LocalReviewQueueTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"pr-inbox-queue-{Guid.NewGuid():N}");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Queue_SerializesRunsAndUpdatesPositions()
    {
        var store = new ReviewRunStore();
        var runner = new BlockingLocalReviewRunner();
        var config = new PrInboxConfig();
        config.LocalReviewer.Model = "qwen2.5-coder-14b";
        var queue = new LocalReviewQueue(
            runner,
            new LocalReviewArtifactStore(store),
            config,
            NullLogger<LocalReviewQueue>.Instance);
        var changedCount = 0;
        queue.Changed += () => Interlocked.Increment(ref changedCount);
        await queue.StartAsync(CancellationToken.None);

        var first = CreateRun(store, 1);
        var second = CreateRun(store, 2);
        var third = CreateRun(store, 3);

        try
        {
            var firstResult = await queue.EnqueueAsync(
                first,
                CancellationToken.None);
            firstResult.QueuePosition.Should().Be(1);
            (await runner.Started.Reader.ReadAsync()).Should().Be(first.RunDirectory);
            queue.Snapshot().Should().ContainSingle().Which.Should()
                .BeEquivalentTo(new LocalReviewQueueItem(
                    first.PrUrl,
                    first.RunDirectory,
                    first.HeadSha,
                    "qwen2.5-coder-14b",
                    LocalReviewQueueItemStatus.Running,
                    Position: 1));

            var secondResult = await queue.EnqueueAsync(
                second,
                CancellationToken.None);
            var thirdResult = await queue.EnqueueAsync(
                third,
                CancellationToken.None);
            secondResult.QueuePosition.Should().Be(2);
            thirdResult.QueuePosition.Should().Be(3);
            store.Get(second.PrUrl)!.LocalReview!.QueuePosition.Should().Be(2);
            store.Get(third.PrUrl)!.LocalReview!.QueuePosition.Should().Be(3);
            queue.Snapshot().Select(item => (item.Status, item.Position, item.PrUrl))
                .Should().Equal(
                    (LocalReviewQueueItemStatus.Running, 1, first.PrUrl),
                    (LocalReviewQueueItemStatus.Queued, 2, second.PrUrl),
                    (LocalReviewQueueItemStatus.Queued, 3, third.PrUrl));

            var duplicate = await queue.EnqueueAsync(
                second,
                CancellationToken.None);
            duplicate.Accepted.Should().BeFalse();

            runner.Release(first.RunDirectory);
            (await runner.Started.Reader.ReadAsync()).Should().Be(second.RunDirectory);
            await WaitUntilAsync(() =>
                store.Get(third.PrUrl)?.LocalReview?.QueuePosition == 2);

            runner.Release(second.RunDirectory);
            (await runner.Started.Reader.ReadAsync()).Should().Be(third.RunDirectory);
            runner.Release(third.RunDirectory);
            await WaitUntilAsync(() => runner.CompletedCount == 3);
            await WaitUntilAsync(() => queue.Snapshot().Count == 0);
            changedCount.Should().BeGreaterThan(3);
        }
        finally
        {
            await queue.StopAsync(CancellationToken.None);
            queue.Dispose();
        }
    }

    private BriefResult CreateRun(ReviewRunStore store, long id)
    {
        var runDir = Path.Combine(_root, id.ToString());
        Directory.CreateDirectory(runDir);
        File.WriteAllText(Path.Combine(runDir, "brief.md"), "# test");
        File.WriteAllText(Path.Combine(runDir, "metadata.json"), "{}");
        var url = $"https://github.com/owner/repo/pull/{id}";
        var brief = new BriefResult(
            id,
            runDir,
            Path.Combine(runDir, "brief.md"),
            Path.Combine(runDir, "metadata.json"),
            $"abcdef{id}",
            url);
        store.StartedRun(new ReviewRun(
            id,
            url,
            runDir,
            brief.HeadSha,
            DateTimeOffset.UtcNow,
            FindingsAtUtc: null,
            Findings: null,
            FindingsErrors: Array.Empty<string>()));
        return brief;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Condition was not reached.");
            }
            await Task.Delay(20);
        }
    }

    private sealed class BlockingLocalReviewRunner : ILocalReviewRunner
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _releases =
            new(StringComparer.OrdinalIgnoreCase);
        private int _completedCount;

        public Channel<string> Started { get; } =
            Channel.CreateUnbounded<string>();

        public int CompletedCount => Volatile.Read(ref _completedCount);

        public async Task RunAsync(BriefResult brief, CancellationToken ct)
        {
            var release = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _releases[brief.RunDirectory] = release;
            await Started.Writer.WriteAsync(brief.RunDirectory, ct);
            await release.Task.WaitAsync(ct);
            Interlocked.Increment(ref _completedCount);
        }

        public void Release(string runDirectory)
        {
            _releases[runDirectory].TrySetResult();
        }
    }
}
