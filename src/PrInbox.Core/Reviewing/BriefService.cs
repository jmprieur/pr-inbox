using System.Text;
using System.Text.Json;
using PrInbox.Core.Models;
using PrInbox.Core.Storage;

namespace PrInbox.Core.Reviewing;

/// <summary>
/// Result of creating a review run: the on-disk artifacts plus the
/// new <c>review_runs</c> row id and PR HEAD that was briefed.
/// </summary>
public sealed record BriefResult(
    long RunId,
    string RunDirectory,
    string BriefPath,
    string MetadataPath,
    string HeadSha,
    string PrUrl);

/// <summary>
/// Reasons <see cref="BriefService.CreateBriefAsync"/> can refuse to
/// build a brief. Callers translate these into user-facing messages.
/// </summary>
public enum BriefCreationFailure
{
    None,
    InvalidUrl,
    NotInInbox,
    NoSnapshot,
}

public sealed class BriefCreationException : Exception
{
    public BriefCreationException(BriefCreationFailure failure, string message) : base(message)
    {
        Failure = failure;
    }
    public BriefCreationFailure Failure { get; }
}

/// <summary>
/// Builds the review brief (<c>brief.md</c>) and metadata for a PR,
/// writes them to <c>%APPDATA%\PrInbox\reviews\&lt;safe-pr&gt;\&lt;ts&gt;_&lt;sha&gt;\</c>,
/// creates a <c>review_runs</c> row, and stamps <c>last_briefed_head_sha</c>
/// on the PR row. Shared by the CLI's <c>review</c> command and the
/// pr-inbox-web companion's "Review" button.
/// </summary>
public sealed class BriefService
{
    private readonly PullRequestRepository _prRepo;
    private readonly PrSnapshotRepository _snapRepo;
    private readonly ObservedThreadRepository _threadRepo;
    private readonly ReviewRunRepository _reviewRepo;

    public BriefService(
        PullRequestRepository prRepo,
        PrSnapshotRepository snapRepo,
        ObservedThreadRepository threadRepo,
        ReviewRunRepository reviewRepo)
    {
        _prRepo = prRepo;
        _snapRepo = snapRepo;
        _threadRepo = threadRepo;
        _reviewRepo = reviewRepo;
    }

    public async Task<BriefResult> CreateBriefAsync(string prUrl, CancellationToken ct)
    {
        if (!PrUrl.TryCanonicalize(prUrl, out var canonical))
        {
            throw new BriefCreationException(BriefCreationFailure.InvalidUrl,
                $"'{prUrl}' is not a recognized PR URL.");
        }

        var row = await _prRepo.GetAsync(canonical, ct);
        if (row is null)
        {
            throw new BriefCreationException(BriefCreationFailure.NotInInbox,
                $"PR '{canonical}' not found in inbox. Run pr-inbox sync first.");
        }

        var snapshot = await _snapRepo.GetLatestAsync(row.Identity, ct);
        if (snapshot is null)
        {
            throw new BriefCreationException(BriefCreationFailure.NoSnapshot,
                "No snapshot available for this PR. Run sync first.");
        }

        var openThreads = await _threadRepo.GetOpenThreadsAsync(row.Identity, ct);
        var since = row.LastBriefedHeadSha is null ? row.FirstSeenAt : row.LastSyncedAt;
        var recentBotThreads = await _threadRepo.GetBotThreadsSinceAsync(row.Identity, since, ct);
        var priorRuns = await _reviewRepo.ListForPrAsync(row.Identity, ct);

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var safePr = MakeSafePathSegment(row.Identity.Url);
        var ts = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ");
        var shaShort = snapshot.HeadSha.Length >= 12 ? snapshot.HeadSha[..12] : snapshot.HeadSha;
        var runDir = Path.Combine(appData, "PrInbox", "reviews", safePr, $"{ts}_{shaShort}");
        Directory.CreateDirectory(runDir);
        var briefPath = Path.Combine(runDir, "brief.md");
        var metadataPath = Path.Combine(runDir, "metadata.json");

        var brief = BuildBriefMarkdown(row, snapshot, openThreads, recentBotThreads, priorRuns);
        await File.WriteAllTextAsync(briefPath, brief, ct);

        var metadata = new
        {
            pr_url = row.Identity.Url,
            stable_identity = row.Identity.Stable,
            source_id = row.SourceId,
            url = row.Url,
            title = row.Title,
            author = row.AuthorLogin,
            head_sha = snapshot.HeadSha,
            base_sha = snapshot.BaseSha,
            last_briefed_head_sha = row.LastBriefedHeadSha,
            last_review_run_head_sha = row.LastReviewRunHeadSha,
            generated_at_utc = DateTimeOffset.UtcNow.ToString("o"),
            run_directory = runDir,
            open_thread_count = openThreads.Count,
            recent_bot_thread_count = recentBotThreads.Count,
            prior_run_count = priorRuns.Count,
        };
        var metadataJson = JsonSerializer.Serialize(metadata,
            new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(metadataPath, metadataJson, ct);

        var runId = await _reviewRepo.InsertAsync(
            row.Identity,
            createdAt: DateTimeOffset.UtcNow,
            briefPath: briefPath,
            runDirectory: runDir,
            headSha: snapshot.HeadSha,
            baseSha: snapshot.BaseSha,
            status: ReviewRunStatus.Generated,
            notes: null,
            ct);

        await _prRepo.UpdateLastBriefedAsync(row.Identity.Url, snapshot.HeadSha, ct);

        return new BriefResult(runId, runDir, briefPath, metadataPath, snapshot.HeadSha, row.Identity.Url);
    }

    /// <summary>Convert a PR URL into a single Windows-safe path segment.</summary>
    public static string MakeSafePathSegment(string url)
    {
        var s = url;
        if (s.StartsWith("https://", StringComparison.Ordinal)) s = s[8..];
        else if (s.StartsWith("http://", StringComparison.Ordinal)) s = s[7..];

        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' or '#' => '_',
                _ => c,
            });
        }
        return sb.ToString();
    }

    private static string BuildBriefMarkdown(
        PullRequestRow pr,
        PrSnapshotRow snapshot,
        IReadOnlyList<ObservedThreadRow> openThreads,
        IReadOnlyList<ObservedThreadRow> recentBotThreads,
        IReadOnlyList<ReviewRunRow> priorRuns)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Review brief — {pr.Identity.Url}");
        sb.AppendLine();
        sb.AppendLine($"_Generated by pr-inbox at {DateTimeOffset.UtcNow:o}._");
        sb.AppendLine();

        sb.AppendLine("## Pull request");
        sb.AppendLine();
        sb.AppendLine($"- **Title:** {pr.Title ?? "(no title)"}");
        sb.AppendLine($"- **Author:** {pr.AuthorLogin ?? "(unknown)"}");
        sb.AppendLine($"- **Repo:** `{pr.DisplayRepo}`");
        sb.AppendLine($"- **URL:** {pr.Url}");
        sb.AppendLine($"- **Stable id:** `{pr.Identity.Stable}`");
        sb.AppendLine($"- **Source:** `{pr.SourceId}` ({pr.SourceKind})");
        sb.AppendLine();

        sb.AppendLine("## State");
        sb.AppendLine();
        sb.AppendLine($"- **HEAD SHA:** `{snapshot.HeadSha}`");
        sb.AppendLine($"- **Base SHA:** `{snapshot.BaseSha}`");
        sb.AppendLine($"- **Commits on this branch:** {snapshot.OrderedCommitShas.Count}");
        sb.AppendLine($"- **Reviewer state:** {snapshot.ReviewerState?.ToString() ?? "unknown"}");
        sb.AppendLine($"- **Last briefed HEAD:** `{pr.LastBriefedHeadSha ?? "(never)"}`");
        sb.AppendLine($"- **Last review-run HEAD:** `{pr.LastReviewRunHeadSha ?? "(never)"}`");
        sb.AppendLine($"- **Last posted-review HEAD:** `{pr.LastPostedReviewHeadSha ?? "(never)"}`");
        sb.AppendLine();

        sb.AppendLine("## What changed since last brief");
        sb.AppendLine();
        if (pr.LastBriefedHeadSha is null)
        {
            sb.AppendLine("First brief generated for this PR — no prior baseline. Review the entire PR.");
        }
        else if (pr.LastBriefedHeadSha == snapshot.HeadSha)
        {
            sb.AppendLine("**No new commits since last brief.** Review activity since last brief is in threads/comments only.");
        }
        else if (!snapshot.OrderedCommitShas.Contains(pr.LastBriefedHeadSha))
        {
            sb.AppendLine("⚠️ **Force-push detected.** The prior HEAD (`" + pr.LastBriefedHeadSha + "`) is not in the current commit list. " +
                "History was rewritten. Treat this as a fresh review against current HEAD.");
        }
        else
        {
            var idx = snapshot.OrderedCommitShas.ToList().IndexOf(pr.LastBriefedHeadSha);
            sb.AppendLine($"**{idx} new commit(s)** added since last brief.");
            sb.AppendLine();
            sb.AppendLine("New commits (newest first):");
            sb.AppendLine();
            foreach (var sha in snapshot.OrderedCommitShas.Take(idx))
            {
                sb.AppendLine($"- `{sha}`");
            }
        }
        sb.AppendLine();

        sb.AppendLine("## Open threads I authored or am party to");
        sb.AppendLine();
        if (openThreads.Count == 0)
        {
            sb.AppendLine("_No open threads._");
        }
        else
        {
            foreach (var t in openThreads)
            {
                sb.AppendLine($"- {t.Kind} from `{t.AuthorLogin}` (first seen {t.FirstSeenAt:yyyy-MM-dd}, last seen {t.LastSeenAt:yyyy-MM-dd})");
            }
        }
        sb.AppendLine();

        sb.AppendLine("## Recent bot activity since last brief");
        sb.AppendLine();
        if (recentBotThreads.Count == 0)
        {
            sb.AppendLine("_No bot activity since last brief._");
        }
        else
        {
            foreach (var t in recentBotThreads)
            {
                sb.AppendLine($"- **{t.BotKind?.ToString() ?? "Other"}** (`{t.AuthorLogin}`, {t.Kind}) — {t.FirstSeenAt:yyyy-MM-dd HH:mm}");
            }
        }
        sb.AppendLine();

        if (priorRuns.Count > 0)
        {
            sb.AppendLine("## Prior review runs");
            sb.AppendLine();
            foreach (var run in priorRuns.Take(5))
            {
                sb.AppendLine($"- Run #{run.Id} ({run.CreatedAt:yyyy-MM-dd HH:mm}, HEAD `{run.HeadSha[..Math.Min(12, run.HeadSha.Length)]}`, status: {run.Status})");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Reviewer instructions");
        sb.AppendLine();
        sb.AppendLine("Use the **dual-model-review** agent with the following parameters:");
        sb.AppendLine();
        sb.AppendLine("- **Reviewers:** Claude Opus 4.7 and GPT-5.5 in parallel.");
        sb.AppendLine("- **Asymmetry instructions:** Opus = exhaustive enumeration of issues at the surface; GPT = lateral procedural / semantic gaps.");
        sb.AppendLine("- **Cap:** 1 round (not our PR; we are reviewing as peers).");
        sb.AppendLine("- **DO NOT post** anything to GitHub or Azure DevOps directly. The pr-inbox companion handles posting after I curate.");
        sb.AppendLine("- For each finding, declare `diff_anchorable: true|false` so the companion can route inline vs body comments.");
        sb.AppendLine("- Apply the **95%+ inline filter**: only mark `diff_anchorable: true` for findings that are high-confidence on the right line of the right file. Architectural / judgment-call findings stay non-anchorable.");
        sb.AppendLine();
        sb.AppendLine("### Required output: findings.yaml");
        sb.AppendLine();
        sb.AppendLine($"Write **structured findings as YAML** to `{{run_dir}}/findings.yaml` (this brief's own directory) matching `findings` schema **v1**. The companion will read it on save and surface counts by severity in the UI; **do not skip this file**, and do not post anything yourself.");
        sb.AppendLine();
        sb.AppendLine("Schema essentials (full schema embedded in `PrInbox.Core` as `findings.schema.json`):");
        sb.AppendLine();
        sb.AppendLine("```yaml");
        sb.AppendLine("schema_version: 1");
        sb.AppendLine($"pr_url: {pr.Identity.Url}");
        sb.AppendLine($"head_sha: {snapshot.HeadSha}");
        sb.AppendLine("generated_at_utc: <ISO-8601 UTC>");
        sb.AppendLine("models: [opus-4.7, gpt-5.5]");
        sb.AppendLine("asymmetry: { both_found: N, opus_only: N, gpt_only: N }");
        sb.AppendLine("findings:");
        sb.AppendLine("  - id: f01                       # stable per-document id");
        sb.AppendLine("    severity: critical            # critical | high | medium | low");
        sb.AppendLine("    confidence: high              # high | medium | low");
        sb.AppendLine("    found_by: [opus, gpt]         # which model(s) raised it");
        sb.AppendLine("    file: src/path/to/File.cs");
        sb.AppendLine("    line: 142                     # optional");
        sb.AppendLine("    line_end: 148                 # optional");
        sb.AppendLine("    diff_anchorable: true         # 95%+ inline filter");
        sb.AppendLine("    title: One-line summary");
        sb.AppendLine("    body: |");
        sb.AppendLine("      Multi-line markdown explanation.");
        sb.AppendLine("    suggested_inline: |           # optional; include the ```suggestion fence");
        sb.AppendLine("      ```suggestion");
        sb.AppendLine("      fixed line of code");
        sb.AppendLine("      ```");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Staleness clause");
        sb.AppendLine();
        sb.AppendLine($"Before posting, verify PR HEAD is still `{snapshot.HeadSha}`. If not, re-run `pr-inbox review {pr.Identity.Url}` to get a fresh brief.");
        sb.AppendLine();

        return sb.ToString();
    }
}
