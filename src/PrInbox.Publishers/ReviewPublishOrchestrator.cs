using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PrInbox.Core.Models;
using PrInbox.Core.Storage;

namespace PrInbox.Publishers;

/// <summary>
/// Public-facing entry point for the web companion. Picks the right
/// <see cref="IPrReviewPublisher"/> for a PR URL, enforces per-PR
/// serialisation (no double-posts from double-clicks), runs the
/// idempotency check, calls the publisher, and writes
/// <see cref="PostedReviewRepository"/> on success.
/// </summary>
public sealed class ReviewPublishOrchestrator
{
    private readonly IPublisherSelector _selector;
    private readonly PullRequestRepository _prRepo;
    private readonly PostedReviewRepository _postedRepo;
    private readonly ILogger<ReviewPublishOrchestrator> _log;

    // One semaphore per PR URL. Ensures a single in-flight publish per PR
    // across the whole process, regardless of how many tabs/clicks.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public ReviewPublishOrchestrator(
        IPublisherSelector selector,
        PullRequestRepository prRepo,
        PostedReviewRepository postedRepo,
        ILogger<ReviewPublishOrchestrator> log)
    {
        _selector = selector;
        _prRepo = prRepo;
        _postedRepo = postedRepo;
        _log = log;
    }

    /// <summary>
    /// Publish (or dry-run) a selection of findings. Always returns —
    /// never throws — and reports failures via <see cref="PublishResult.Errors"/>.
    /// </summary>
    public async Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken ct)
    {
        IPrReviewPublisher publisher;
        try { publisher = _selector.Select(request.PrUrl); }
        catch (Exception ex)
        {
            return PublishResult.Failure(_selector.IdentityForLogging(request.PrUrl) ?? "unknown",
                $"Cannot select a publisher for {request.PrUrl}: {ex.Message}");
        }

        var gate = _locks.GetOrAdd(request.PrUrl, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await PublishUnderLockAsync(publisher, request, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<PublishResult> PublishUnderLockAsync(
        IPrReviewPublisher publisher, PublishRequest request, CancellationToken ct)
    {
        // 1. Look up PrIdentity so we can write posted_reviews referencing
        //    a valid pr_identity row.
        var prRow = await _prRepo.GetAsync(request.PrUrl, ct);
        if (prRow is null)
        {
            return PublishResult.Failure("unknown",
                $"PR {request.PrUrl} not found in inbox; sync the inbox first.");
        }

        // 1b. Re-resolve publisher using the PR's recorded identity, in
        //     case the URL-only selector picked a different identity for
        //     hosts with multiple registered identities (github.com).
        try
        {
            publisher = _selector.SelectFor(request.PrUrl, prRow.IdentityUsed);
        }
        catch
        {
            // Fall back to the URL-only selection if no exact match.
        }

        // 2. Filter out findings whose id or fingerprint already exists in
        //    posted_reviews. Skip set fed back into the result.
        var (postedIds, postedFps) = await _postedRepo.GetPostedFindingsForPrAsync(prRow.Identity, ct);
        var freshFindings = new List<FindingToPost>(request.Findings.Count);
        var skipped = 0;
        foreach (var f in request.Findings)
        {
            var fp = PublishHelpers.FingerprintOf(f);
            if ((!string.IsNullOrEmpty(f.Id) && postedIds.Contains(f.Id)) || postedFps.Contains(fp))
            {
                skipped++;
                continue;
            }
            freshFindings.Add(f);
        }

        if (freshFindings.Count == 0)
        {
            // Nothing to do; still useful to report skipped count.
            return new PublishResult(
                Posted: false,
                PlatformReviewId: null,
                ReviewUrl: null,
                InlineCount: 0,
                BodyOnlyCount: 0,
                SkippedAsAlreadyPosted: skipped,
                HeadShaAtPost: null,
                HeadChanged: false,
                IdentityUsed: publisher.Kind,
                Warnings: new[] { $"All {skipped} selected finding(s) already posted; nothing to publish." },
                Errors: Array.Empty<string>());
        }

        // 3. Delegate to the publisher.
        var filteredRequest = request with { Findings = freshFindings };
        var result = await publisher.PublishAsync(filteredRequest, ct);
        result = result with { SkippedAsAlreadyPosted = skipped };

        // 4. On success and live mode, record what we just posted.
        if (result.Posted && !request.DryRun && !string.IsNullOrEmpty(result.PlatformReviewId))
        {
            var findingIds = freshFindings.Where(f => !string.IsNullOrEmpty(f.Id)).Select(f => f.Id).ToArray();
            var fingerprints = freshFindings.Select(PublishHelpers.FingerprintOf).ToArray();
            try
            {
                await _postedRepo.InsertAsync(
                    identity: prRow.Identity,
                    reviewRunId: request.RunId,
                    platformReviewId: result.PlatformReviewId!,
                    reviewUrl: result.ReviewUrl,
                    postedAt: DateTimeOffset.UtcNow,
                    headShaAtPost: result.HeadShaAtPost ?? request.HeadShaAtAuthoring,
                    identityUsed: result.IdentityUsed,
                    inlineCount: result.InlineCount,
                    bodyPresent: !string.IsNullOrWhiteSpace(request.ReviewBodyHeader),
                    findingIds: findingIds,
                    findingFingerprints: fingerprints,
                    ct: ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Posted to platform but FAILED to record in posted_reviews for {Url}", request.PrUrl);
                var amendedWarnings = result.Warnings.Concat(new[]
                {
                    $"Post succeeded but local recording failed: {ex.Message}. Re-posting may duplicate comments."
                }).ToArray();
                result = result with { Warnings = amendedWarnings };
            }
        }

        return result;
    }
}

/// <summary>
/// Picks the right publisher implementation given a PR URL and the
/// identity that observed it (so we post with the correct token).
/// </summary>
public interface IPublisherSelector
{
    /// <summary>
    /// Select by URL alone (uses the inbox's recorded identity). Throws if
    /// nothing matches.
    /// </summary>
    IPrReviewPublisher Select(string prUrl);

    /// <summary>
    /// Select for a specific (URL, identity) pair. Used by the orchestrator
    /// after it has loaded the PR row.
    /// </summary>
    IPrReviewPublisher SelectFor(string prUrl, string identityUsed);

    /// <summary>Best-effort identity tag for log/error contexts.</summary>
    string? IdentityForLogging(string prUrl);
}
