using PrInbox.Core.Storage;

namespace PrInbox.Web.Services;

/// <summary>
/// Drift state of a PR's current HEAD relative to the HEAD at the last
/// review run. Used by the inbox to surface "you might need to re-review"
/// without forcing the user to inspect every PR.
/// </summary>
public enum DriftKind
{
    /// <summary>No snapshot available yet, so drift cannot be computed.</summary>
    Unknown,
    /// <summary>No prior review run — the PR has never been reviewed by us.</summary>
    NeverReviewed,
    /// <summary>Last reviewed HEAD equals current HEAD — nothing to do.</summary>
    Clean,
    /// <summary>Last reviewed HEAD is in the current commit list but isn't
    /// HEAD. <see cref="DriftInfo.CommitsAhead"/> says how many.</summary>
    Ahead,
    /// <summary>Last reviewed HEAD is NOT in the current commit list,
    /// which means the branch was force-pushed since we reviewed.</summary>
    ForcePushed,
}

/// <summary>
/// Per-row drift summary. Computed by <see cref="Compute"/> from a
/// <see cref="PullRequestRow"/> and the most recent
/// <see cref="PrSnapshotRow"/> for the same identity.
/// </summary>
public sealed record DriftInfo(
    DriftKind Kind,
    int CommitsAhead,
    string? LastReviewedHeadSha,
    string? CurrentHeadSha)
{
    public static readonly DriftInfo Unknown = new(DriftKind.Unknown, 0, null, null);

    /// <summary>
    /// Compute drift. Treats <c>LastReviewRunHeadSha</c> as the anchor
    /// (a review was run — whether or not the result was posted). Force-push
    /// is detected by absence of the anchor SHA from the snapshot's commit
    /// list. "Commits ahead" is the position of the anchor in the
    /// newest-first commit list, mirroring the existing CLI convention.
    /// </summary>
    public static DriftInfo Compute(PullRequestRow pr, PrSnapshotRow? snap)
        => Compute(pr.LastReviewRunHeadSha, snap);

    /// <summary>
    /// Same as <see cref="Compute(PullRequestRow, PrSnapshotRow?)"/> but with
    /// a caller-supplied anchor — used by the Review page to compare against
    /// the specific run's head sha rather than the row's "last run" sha.
    /// </summary>
    public static DriftInfo Compute(string? lastReviewedSha, PrSnapshotRow? snap)
    {
        var current = snap?.HeadSha;

        if (string.IsNullOrEmpty(lastReviewedSha))
        {
            return new DriftInfo(DriftKind.NeverReviewed, 0, null, current);
        }
        if (snap is null || string.IsNullOrEmpty(current))
        {
            return new DriftInfo(DriftKind.Unknown, 0, lastReviewedSha, null);
        }
        if (string.Equals(lastReviewedSha, current, StringComparison.Ordinal))
        {
            return new DriftInfo(DriftKind.Clean, 0, lastReviewedSha, current);
        }

        var commits = snap.OrderedCommitShas;
        if (commits is null || commits.Count == 0)
        {
            return new DriftInfo(DriftKind.Unknown, 0, lastReviewedSha, current);
        }

        var idx = -1;
        for (var i = 0; i < commits.Count; i++)
        {
            if (string.Equals(commits[i], lastReviewedSha, StringComparison.Ordinal))
            {
                idx = i;
                break;
            }
        }
        if (idx < 0)
        {
            return new DriftInfo(DriftKind.ForcePushed, 0, lastReviewedSha, current);
        }
        // Newest-first list: items at index < idx are newer than the
        // anchor, so idx itself is the count of commits since the review
        // (guaranteed >= 1 here because anchor != current).
        return new DriftInfo(DriftKind.Ahead, Math.Max(idx, 1), lastReviewedSha, current);
    }

    /// <summary>
    /// Build a platform-appropriate URL to view what has changed since
    /// the last review. Returns null when the PR is not on a platform we
    /// know how to compare for, or when no anchor SHA exists.
    /// </summary>
    public string? BuildCompareUrl(string prUrl)
    {
        if (string.IsNullOrEmpty(LastReviewedHeadSha) || string.IsNullOrEmpty(CurrentHeadSha)) return null;
        if (string.IsNullOrEmpty(prUrl)) return null;

        // GitHub: https://github.com/{owner}/{repo}/pull/N → /compare/{a}..{b}
        // Match GitHub.com and Enterprise alike — same path shape.
        var m = System.Text.RegularExpressions.Regex.Match(
            prUrl,
            @"^(https?://[^/]+/[^/]+/[^/]+)/pull/\d+",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success)
        {
            return $"{m.Groups[1].Value}/compare/{LastReviewedHeadSha}...{CurrentHeadSha}";
        }

        // ADO not yet supported — branchCompare URLs require project context
        // we don't reliably have here. Returning null disables the link.
        return null;
    }
}
