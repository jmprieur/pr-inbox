namespace PrInbox.Web.Services;

/// <summary>
/// Abstraction over launching a dual-model-review session in a new
/// console window. Chunk 5 ships a no-op implementation that records
/// the request; chunk 6 will wire <c>wt.exe</c> + clipboard staging.
/// </summary>
public interface IReviewLauncher
{
    /// <summary>Spawn (or pretend to spawn) a review session for the PR.</summary>
    /// <returns>A short user-visible message describing what happened.</returns>
    Task<string> LaunchAsync(string prUrl, CancellationToken ct);
}

/// <summary>
/// Placeholder launcher. Logs the request and returns a friendly message.
/// Chunk 6 replaces this with the real <c>wt.exe</c> spawn.
/// </summary>
public sealed class ReviewLauncher : IReviewLauncher
{
    private readonly ILogger<ReviewLauncher> _log;

    public ReviewLauncher(ILogger<ReviewLauncher> log) => _log = log;

    public Task<string> LaunchAsync(string prUrl, CancellationToken ct)
    {
        _log.LogInformation("Review requested for {Url} (launcher not yet wired — chunk 6).", prUrl);
        return Task.FromResult($"Review request recorded for {prUrl}. " +
            "(Console launcher wires in chunk 6.)");
    }
}
