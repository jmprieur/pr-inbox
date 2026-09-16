using System.Collections.Concurrent;
using PrInbox.Core.Credentials;
using PrInbox.Core.Reviewing;

namespace PrInbox.Web.Services;

public sealed class LocalReviewArtifactStore
{
    private readonly ReviewRunStore _runs;

    public LocalReviewArtifactStore(ReviewRunStore runs)
    {
        _runs = runs;
    }

    public async Task WriteAsync(
        BriefResult brief,
        LocalReviewArtifact artifact,
        CancellationToken ct)
    {
        var path = Path.Combine(brief.RunDirectory, LocalReviewArtifact.FileName);
        var tempPath = path + ".tmp";
        var json = System.Text.Json.JsonSerializer.Serialize(
            artifact,
            LocalReviewArtifact.JsonOptions);
        await File.WriteAllTextAsync(tempPath, json, ct);
        File.Move(tempPath, path, overwrite: true);
        _runs.UpdateLocalReview(brief.PrUrl, brief.RunDirectory, artifact);
    }
}

public sealed record LocalReviewEnqueueResult(
    bool Accepted,
    int? QueuePosition,
    string Message);

public interface ILocalReviewQueue
{
    Task<LocalReviewEnqueueResult> EnqueueAsync(
        BriefResult brief,
        CancellationToken ct);
}

/// <summary>
/// Single-worker queue for local inference. Foundry Local is optimized for
/// one interactive user and does not provide server-style concurrent request
/// scheduling, so cloud reviews remain parallel while local model work is
/// serialized here.
/// </summary>
public sealed class LocalReviewQueue : BackgroundService, ILocalReviewQueue
{
    private readonly object _gate = new();
    private readonly LinkedList<BriefResult> _waiting = new();
    private readonly ConcurrentDictionary<string, byte> _known =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly ILocalReviewRunner _runner;
    private readonly LocalReviewArtifactStore _artifacts;
    private readonly PrInboxConfig _config;
    private readonly ILogger<LocalReviewQueue> _log;
    private BriefResult? _active;

    public LocalReviewQueue(
        ILocalReviewRunner runner,
        LocalReviewArtifactStore artifacts,
        PrInboxConfig config,
        ILogger<LocalReviewQueue> log)
    {
        _runner = runner;
        _artifacts = artifacts;
        _config = config;
        _log = log;
    }

    public async Task<LocalReviewEnqueueResult> EnqueueAsync(
        BriefResult brief,
        CancellationToken ct)
    {
        if (!_known.TryAdd(brief.RunDirectory, 0))
        {
            return new LocalReviewEnqueueResult(
                Accepted: false,
                QueuePosition: null,
                Message: "The local shadow review is already queued or running.");
        }

        List<(BriefResult Brief, int Position)> positions;
        int position;
        lock (_gate)
        {
            _waiting.AddLast(brief);
            positions = SnapshotQueuedPositionsLocked();
            position = positions.Single(item =>
                string.Equals(
                    item.Brief.RunDirectory,
                    brief.RunDirectory,
                    StringComparison.OrdinalIgnoreCase)).Position;
        }

        try
        {
            await PublishQueuedPositionsAsync(positions, ct);
            _signal.Release();
            return new LocalReviewEnqueueResult(
                Accepted: true,
                QueuePosition: position,
                Message: position == 1
                    ? "Local shadow review queued and starting."
                    : $"Local shadow review queued at position {position}.");
        }
        catch
        {
            lock (_gate)
            {
                var node = _waiting.First;
                while (node is not null)
                {
                    if (string.Equals(
                            node.Value.RunDirectory,
                            brief.RunDirectory,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        _waiting.Remove(node);
                        break;
                    }
                    node = node.Next;
                }
            }
            _known.TryRemove(brief.RunDirectory, out _);
            throw;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await _signal.WaitAsync(stoppingToken);

            BriefResult? brief;
            List<(BriefResult Brief, int Position)> positions;
            lock (_gate)
            {
                brief = _waiting.First?.Value;
                if (brief is not null)
                {
                    _waiting.RemoveFirst();
                    _active = brief;
                }
                positions = SnapshotQueuedPositionsLocked();
            }
            if (brief is null) continue;

            await PublishQueuedPositionsAsync(positions, stoppingToken);
            try
            {
                await _runner.RunAsync(brief, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(
                    ex,
                    "Queued local review failed unexpectedly for {PrUrl}",
                    brief.PrUrl);
            }
            finally
            {
                lock (_gate)
                {
                    _active = null;
                    positions = SnapshotQueuedPositionsLocked();
                }
                _known.TryRemove(brief.RunDirectory, out _);
                try
                {
                    await PublishQueuedPositionsAsync(
                        positions,
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Could not refresh local review queue positions.");
                }
            }
        }
    }

    private List<(BriefResult Brief, int Position)> SnapshotQueuedPositionsLocked()
    {
        var position = _active is null ? 1 : 2;
        var result = new List<(BriefResult, int)>(_waiting.Count);
        foreach (var brief in _waiting)
        {
            result.Add((brief, position++));
        }
        return result;
    }

    private async Task PublishQueuedPositionsAsync(
        IReadOnlyList<(BriefResult Brief, int Position)> positions,
        CancellationToken ct)
    {
        foreach (var item in positions)
        {
            await _artifacts.WriteAsync(item.Brief, new LocalReviewArtifact
            {
                Status = LocalReviewStatus.Queued,
                Model = _config.LocalReviewer.Model,
                HeadSha = item.Brief.HeadSha,
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                QueuePosition = item.Position,
            }, ct);
        }
    }
}
