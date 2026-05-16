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

        var brief = BuildBriefMarkdown(row, snapshot, openThreads, recentBotThreads, priorRuns, runDir);
        await File.WriteAllTextAsync(briefPath, brief, ct);

        // Copy the findings schema next to the brief so the agent can
        // resolve it via `./findings.schema.json` from the run directory
        // (the brief's cwd). Single source of truth: PrInbox.Core's embedded
        // resource. If copy fails (sandboxed FS, etc.) the brief still wins —
        // worst case the agent loses schema validation, not the review itself.
        try
        {
            await CopyEmbeddedSchemaAsync(Path.Combine(runDir, "findings.schema.json"), ct);
        }
        catch (IOException)
        {
            // Best-effort; brief is the primary contract.
        }

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

    private static async Task CopyEmbeddedSchemaAsync(string destPath, CancellationToken ct)
    {
        var asm = typeof(BriefService).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("findings.schema.json", StringComparison.Ordinal));
        if (name is null) return;
        await using var src = asm.GetManifestResourceStream(name);
        if (src is null) return;
        await using var dst = File.Create(destPath);
        await src.CopyToAsync(dst, ct);
    }

    private static string BuildBriefMarkdown(
        PullRequestRow pr,
        PrSnapshotRow snapshot,
        IReadOnlyList<ObservedThreadRow> openThreads,
        IReadOnlyList<ObservedThreadRow> recentBotThreads,
        IReadOnlyList<ReviewRunRow> priorRuns,
        string runDir)
    {
        var sb = new StringBuilder();

        var shortHead = ShortSha(snapshot.HeadSha);
        var shortBase = ShortSha(snapshot.BaseSha);
        var (botCallout, authorBotSuffix) = DetectBotPr(pr.Title, pr.AuthorLogin);

        // Header — single-line dossier summary the reviewer reads first.
        sb.AppendLine($"# {pr.DisplayRepo} #{pr.Number} — {pr.Title ?? "(no title)"}");
        if (botCallout is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"**{botCallout}**");
        }
        sb.AppendLine();
        sb.AppendLine($"_Generated by pr-inbox at {DateTimeOffset.UtcNow:o}._");
        sb.AppendLine();
        sb.AppendLine($"- **Author:** `{pr.AuthorLogin ?? "(unknown)"}`{authorBotSuffix}");
        sb.AppendLine($"- **URL:** {pr.Url}");
        sb.AppendLine($"- **Stable id:** `{pr.Identity.Stable}` · source `{pr.SourceId}` ({pr.SourceKind})");
        sb.AppendLine($"- **HEAD** `{shortHead}` · **Base** `{shortBase}` · commits: {snapshot.OrderedCommitShas.Count} · reviewer state: {snapshot.ReviewerState?.ToString() ?? "unknown"}");
        sb.AppendLine($"- **Last briefed:** `{ShortShaOrNever(pr.LastBriefedHeadSha)}` · **last review-run:** `{ShortShaOrNever(pr.LastReviewRunHeadSha)}` · **last posted:** `{ShortShaOrNever(pr.LastPostedReviewHeadSha)}`");
        sb.AppendLine();

        sb.AppendLine("## What changed since last brief");
        sb.AppendLine();
        if (pr.LastBriefedHeadSha is null)
        {
            sb.AppendLine("First brief for this PR — no prior baseline. Review the entire PR.");
        }
        else if (pr.LastBriefedHeadSha == snapshot.HeadSha)
        {
            sb.AppendLine("**No new commits since last brief.** Review activity since last brief is in threads/comments only.");
        }
        else if (!snapshot.OrderedCommitShas.Contains(pr.LastBriefedHeadSha))
        {
            sb.AppendLine($"⚠️ **Force-push detected.** Prior HEAD `{ShortSha(pr.LastBriefedHeadSha)}` is not in the current commit list. Treat as a fresh review against current HEAD.");
        }
        else
        {
            var idx = snapshot.OrderedCommitShas.ToList().IndexOf(pr.LastBriefedHeadSha);
            sb.AppendLine($"**{idx} new commit(s)** added since last brief (newest first):");
            sb.AppendLine();
            foreach (var sha in snapshot.OrderedCommitShas.Take(idx))
            {
                sb.AppendLine($"- `{sha}`");
            }
        }
        sb.AppendLine();

        sb.AppendLine("## Open threads");
        sb.AppendLine();
        if (openThreads.Count == 0)
        {
            sb.AppendLine("_No open threads._");
        }
        else
        {
            foreach (var t in openThreads)
            {
                sb.AppendLine($"- `{t.AuthorLogin ?? "(unknown)"}` · {t.Kind} · first seen {t.FirstSeenAt:yyyy-MM-dd}, last seen {t.LastSeenAt:yyyy-MM-dd}");
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
                sb.AppendLine($"- **{t.BotKind?.ToString() ?? "Other"}** · `{t.AuthorLogin ?? "(unknown)"}` · {t.Kind} · {t.FirstSeenAt:yyyy-MM-dd HH:mm}");
            }
        }
        sb.AppendLine();

        if (priorRuns.Count > 0)
        {
            sb.AppendLine("## Prior review runs");
            sb.AppendLine();
            foreach (var run in priorRuns.Take(5))
            {
                sb.AppendLine($"- Run #{run.Id} · {run.CreatedAt:yyyy-MM-dd HH:mm} · HEAD `{ShortSha(run.HeadSha)}` · {run.Status}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Output contract");
        sb.AppendLine();
        sb.AppendLine($"Write `findings.yaml` (schema v1, see `./findings.schema.json` next to this brief) to this run directory:");
        sb.AppendLine();
        sb.AppendLine($"`{runDir}`");
        sb.AppendLine();
        sb.AppendLine($"Before writing, verify PR HEAD is still `{snapshot.HeadSha}`. If not, re-run `pr-inbox review {pr.Identity.Url}`.");
        sb.AppendLine();
        sb.AppendLine("**Do not post.** The pr-inbox companion reads `findings.yaml` and posts after I curate.");

        return sb.ToString();
    }

    private static string ShortSha(string sha) =>
        string.IsNullOrEmpty(sha) ? "(none)" : (sha.Length >= 12 ? sha[..12] : sha);

    private static string ShortShaOrNever(string? sha) =>
        sha is null ? "never" : ShortSha(sha);

    /// <summary>
    /// Detects bot-generated PRs from title heuristics or author login.
    /// Returns (callout, suffix) where <c>callout</c> is the first-line banner
    /// ("Generated by &lt;tool&gt;"), and <c>suffix</c> is appended to the author
    /// bullet (" *(bot)*"). Returns (null, "") if no signal found.
    /// </summary>
    private static (string? callout, string authorSuffix) DetectBotPr(string? title, string? author)
    {
        var authorIsBot = author is not null &&
            author.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase);

        string? callout = null;
        if (!string.IsNullOrEmpty(title))
        {
            // "Generated by <X>: …" — common SRE / agent / dependabot framing.
            var m = System.Text.RegularExpressions.Regex.Match(title,
                @"Generated by ([^:\.\-—]+?)(?:[:\.\-—]|$)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
            {
                callout = $"[Generated by {m.Groups[1].Value.Trim()}]";
            }
            else if (System.Text.RegularExpressions.Regex.IsMatch(title,
                @"^chore\(deps(-dev)?\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                callout = "[Dependabot / automated dependency bump]";
            }
            else if (authorIsBot)
            {
                callout = $"[Automated PR by `{author}`]";
            }
        }
        else if (authorIsBot)
        {
            callout = $"[Automated PR by `{author}`]";
        }

        var suffix = authorIsBot ? " *(bot)*" : string.Empty;
        return (callout, suffix);
    }
}
