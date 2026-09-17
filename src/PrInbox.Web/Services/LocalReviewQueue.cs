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

public enum LocalReviewQueueItemStatus
{
    Running,
    Queued,
}

public sealed record LocalReviewQueueItem(
    string PrUrl,
    string RunDirectory,
    string HeadSha,
    string Model,
    LocalReviewQueueItemStatus Status,
    int Position,
    LocalReviewPhase? Phase,
    int? ProgressCurrent,
    int? ProgressTotal,
    string? ProgressDetail);

public interface ILocalReviewQueue
{
    event Action? Changed;

    Task<LocalReviewEnqueueResult> EnqueueAsync(
        BriefResult brief,
        CancellationToken ct);

    IReadOnlyList<LocalReviewQueueItem> Snapshot();
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
    private readonly ReviewRunStore _runs;
    private readonly PrInboxConfig _config;
    private readonly ILogger<LocalReviewQueue> _log;
    private BriefResult? _active;

    public event Action? Changed;

    public LocalReviewQueue(
        ILocalReviewRunner runner,
        LocalReviewArtifactStore artifacts,
        ReviewRunStore runs,
        PrInboxConfig config,
        ILogger<LocalReviewQueue> log)
    {
        _runner = runner;
        _artifacts = artifacts;
        _runs = runs;
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
            RaiseChanged();
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
            RaiseChanged();
            throw;
        }
    }

    public IReadOnlyList<LocalReviewQueueItem> Snapshot()
    {
        lock (_gate)
        {
            var result = new List<LocalReviewQueueItem>(
                _waiting.Count + (_active is null ? 0 : 1));
            if (_active is not null)
            {
                result.Add(ToQueueItem(
                    _active,
                    LocalReviewQueueItemStatus.Running,
                    position: 1));
            }
            var position = _active is null ? 1 : 2;
            foreach (var brief in _waiting)
            {
                result.Add(ToQueueItem(
                    brief,
                    LocalReviewQueueItemStatus.Queued,
                    position++));
            }
            return result;
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
            RaiseChanged();
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
                RaiseChanged();
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

    private LocalReviewQueueItem ToQueueItem(
        BriefResult brief,
        LocalReviewQueueItemStatus status,
        int position)
    {
        var progress = _runs.Get(brief.PrUrl)?.LocalReview;
        return new LocalReviewQueueItem(
            brief.PrUrl,
            brief.RunDirectory,
            brief.HeadSha,
            _config.LocalReviewer.Model,
            status,
            position,
            progress?.Phase,
            progress?.ProgressCurrent,
            progress?.ProgressTotal,
            progress?.ProgressDetail);
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
                Phase = LocalReviewPhase.Queued,
                ProgressCurrent = item.Position,
                ProgressTotal = positions.Count + (_active is null ? 0 : 1),
                ProgressDetail =
                    $"Queued at position {item.Position}.",
            }, ct);
        }
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch { }
    }
}
