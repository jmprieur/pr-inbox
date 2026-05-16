using System.ComponentModel;
using System.Text;
using PrInbox.Core.Models;
using PrInbox.Core.Storage;
using PrInbox.Sources;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PrInbox.Cli.Commands;

internal sealed class ReviewSettings : CommandSettings
{
    [CommandArgument(0, "<PR_URL>")]
    [Description("PR URL, e.g. https://github.com/owner/repo/pull/42")]
    public required string PrId { get; init; }

    [CommandOption("--refresh")]
    [Description("Re-sync this PR before generating the brief.")]
    public bool Refresh { get; init; } = true;
}

internal sealed class ReviewCommand : AsyncCommand<ReviewSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ReviewSettings settings)
    {
        var db = new PrInboxDb(PrInboxDb.DefaultUserConnectionString());
        await new MigrationRunner().MigrateAsync(db.ConnectionString);

        var prRepo = new PullRequestRepository(db);
        var snapRepo = new PrSnapshotRepository(db);
        var threadRepo = new ObservedThreadRepository(db);
        var reviewRepo = new ReviewRunRepository(db);

        // Accept either a canonical URL or a non-canonical one — canonicalize first.
        string lookupUrl;
        if (PrUrl.TryCanonicalize(settings.PrId, out var canonical))
        {
            lookupUrl = canonical;
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]'{Markup.Escape(settings.PrId)}' is not a recognized PR URL.[/] " +
                "Expected something like [grey]https://github.com/owner/repo/pull/42[/].");
            return 1;
        }

        var row = await prRepo.GetAsync(lookupUrl, CancellationToken.None);
        if (row is null)
        {
            AnsiConsole.MarkupLine($"[red]PR '{Markup.Escape(lookupUrl)}' not found in inbox.[/] Run [bold]pr-inbox sync[/] first.");
            return 1;
        }

        var snapshot = await snapRepo.GetLatestAsync(row.Identity, CancellationToken.None);
        if (snapshot is null)
        {
            AnsiConsole.MarkupLine("[red]No snapshot available for this PR. Run sync first.[/]");
            return 1;
        }

        var openThreads = await threadRepo.GetOpenThreadsAsync(row.Identity, CancellationToken.None);
        var since = row.LastBriefedHeadSha is null ? row.FirstSeenAt : row.LastSyncedAt;
        var recentBotThreads = await threadRepo.GetBotThreadsSinceAsync(row.Identity, since, CancellationToken.None);
        var priorRuns = await reviewRepo.ListForPrAsync(row.Identity, CancellationToken.None);

        // Create immutable run directory.
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var safePr = MakeSafePathSegment(row.Identity.Url);
        var ts = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ");
        var shaShort = snapshot.HeadSha.Length >= 12 ? snapshot.HeadSha[..12] : snapshot.HeadSha;
        var runDir = Path.Combine(appData, "PrInbox", "reviews", safePr, $"{ts}_{shaShort}");
        Directory.CreateDirectory(runDir);
        var briefPath = Path.Combine(runDir, "brief.md");
        var metadataPath = Path.Combine(runDir, "metadata.json");

        var brief = BuildBriefMarkdown(row, snapshot, openThreads, recentBotThreads, priorRuns);
        await File.WriteAllTextAsync(briefPath, brief, CancellationToken.None);

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
        var metadataJson = System.Text.Json.JsonSerializer.Serialize(metadata,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(metadataPath, metadataJson, CancellationToken.None);

        var runId = await reviewRepo.InsertAsync(
            row.Identity,
            createdAt: DateTimeOffset.UtcNow,
            briefPath: briefPath,
            runDirectory: runDir,
            headSha: snapshot.HeadSha,
            baseSha: snapshot.BaseSha,
            status: ReviewRunStatus.Generated,
            notes: null,
            CancellationToken.None);

        await prRepo.UpdateLastBriefedAsync(row.Identity.Url, snapshot.HeadSha, CancellationToken.None);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]Review run #{runId} created.[/]");
        AnsiConsole.MarkupLine($"  brief:    [cyan]{Markup.Escape(briefPath)}[/]");
        AnsiConsole.MarkupLine($"  metadata: [cyan]{Markup.Escape(metadataPath)}[/]");
        AnsiConsole.MarkupLine($"  HEAD:     [white]{Markup.Escape(snapshot.HeadSha)}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Recommended invocation:[/]");
        AnsiConsole.MarkupLine($"  [grey]copilot --prompt \"{Markup.Escape(briefPath)}\"[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey](Copy the path into a fresh Copilot session and ask it to use the dual-model-review agent.)[/]");
        return 0;
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

        // Diff-since-last summary.
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

    private static string MakeSafePathSegment(string url)
    {
        // The URL becomes a Windows path segment for the run directory.
        // Strip the https:// prefix and replace path separators with underscores.
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
}
