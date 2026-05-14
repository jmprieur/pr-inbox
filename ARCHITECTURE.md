# pr-inbox — Architecture

*Design rationale, decisions made, and the rubber-duck critique log.
Read alongside `README.md` (user-facing) and `AMBIGUITIES.md` (open decisions).*

---

## Origin

Jean-Marc reviews many PRs at scale across `github.com`, GitHub Enterprise
(Microsoft's GHE), and Azure DevOps. The `dual-model-review` skill (Opus 4.7 +
GPT-5.5) is mature (N=6, asymmetry pattern stable as of PRs #4133/#51/#53/#4248).
The **harness** is not.

`pr-inbox` is that harness. It does not perform the review — it
aggregates the inbox, mirrors per-PR state across sessions, and bootstraps
a Copilot session with full context. The CLI is the UI; SQLite is the truth.

---

## Decisions and why

### Why CLI + SQLite (not a web/TUI dashboard)

Jean-Marc lives in terminals and Copilot sessions. A daemon-with-dashboard
introduces operational overhead; a CLI that piggybacks on the existing Copilot
session model is native. SQLite is appropriate for a personal operator tool —
enough structure for state, sync status, and future telemetry, no service
infrastructure to run.

### Why C# .NET 10 (not Python or PowerShell)

- Auditability + type safety + idiomatic for a Microsoft.Identity-team author.
- Could grow into a shipped tool; .NET ecosystem fits.
- PowerShell 5.1 has known encoding traps (see `current.md` 2026-05-13 lesson
  on `az repos pr update --description` + em-dash mojibake) that would bite
  us across the whole tool surface.
- Python is fastest to iterate, but the tradeoff cost of two ecosystems on
  Jean-Marc's machine + cross-platform packaging is real.

### Why source adapters behind `IPrReadSource` with a `Capabilities` object

- Three platforms with different shapes (Azure DevOps has no global reviewer
  inbox; GitHub has stable node IDs; ADO has different thread semantics).
- Capability flags prevent ADO's limitations from leaking everywhere as
  ad-hoc conditionals. Example: `list` queries
  `SupportsGlobalReviewerInbox` and falls back to per-project enumeration
  when it's `false`.
- v0.1 is read-only **by construction**: `IPrReadSource` has no write methods.
  A future `IPrReviewPublisher` is a separate type the v0.1 binary cannot
  accidentally call.

### Why delegate credentials to `gh` + Azure CLI

`pr-inbox` stores no tokens. Reads them on demand from:

- `gh auth token --hostname <host>` (GitHub.com + GHE; one identity per host)
- `Azure.Identity.AzureCliCredential` for ADO (resource id
  `499b84ac-1321-427f-aa17-267ca6975798`)

Eliminates the entire class of "PAT leakage in config" risk, removes any need
for Windows Credential Manager wrapping, and aligns with the tools Jean-Marc
already uses to authenticate. The cost is a hard dependency on `gh` and `az`
being installed and authenticated, validated by `config doctor`.

### Why append-only snapshot/event model (not "current-row" mirror)

The rubber-duck pass flagged this as a top blocker. A "current-row" mirror with
`last_seen_author_commit_sha` and `my_open_thread_ids` JSON answers "what was
the latest thing I remembered?" but **not** "what changed since my last
review?" — exactly the question v0.2 follow-up needs.

Solution:

| Table | Cardinality | Purpose |
|---|---|---|
| `pull_requests` | 1 per PR | Fast triage lookup, current-row truth |
| `pr_snapshots` | N per PR | Platform state at each successful sync |
| `observed_threads` | N per PR | Per-thread observation, computes "unread" as derived query |
| `review_runs` | N per PR | Immutable record of each `review` invocation |
| `posted_reviews` | (v0.2+) | Reviews I posted, linked back to a `review_runs` row |
| `sync_runs` | N per source/identity | Per-attempt status, partial-failure visibility |

Snapshots dedupe: only insert if any tracked field changed. Otherwise just
bump `pull_requests.last_synced_at`.

### Why immutable review-run directories

`pr-inbox review <id>` could overwrite `brief.md` each time. The rubber-duck
critique correctly flagged this as a state continuity hole. Three problems:

1. Which brief is canonical a week later?
2. Re-running review against new head SHA destroys the prior context.
3. A later Copilot session can't tell if it's reviewing the same HEAD or stale.

Solution: `%APPDATA%\PrInbox\reviews\<pr_dir>\<UTC-ts>_<head_sha[:12]>\`.
Re-running `review` **always** appends a new immutable run, registered in
the `review_runs` table. Future `--launch` flag, future `latest` symlink,
future `clean` verb all live above an immutable substrate.

### Why "tracking" vs "assigned"

If `sync` only pulls currently-assigned PRs, follow-up work is silently
dropped (assignment changes, threads remain open, Copilot keeps commenting).

The registry keeps a PR until **explicitly archived**:

| `tracking_reason` | When |
|---|---|
| `assigned` | Currently a reviewer on this PR |
| `previously_assigned` | Was a reviewer; still active threads or recent commits |
| `manually_added` | User added via `pr-inbox add` (v0.2+) |
| `archived` | User-archived; ignored by default in `list` |

Status (`open`/`closed`/`merged`/`inaccessible`) is orthogonal.

### Why force-push detection lives in v0.1

`last_briefed_head_sha == current_head_sha` is insufficient. If the author
force-pushed, the prior reviewed SHA may no longer be reachable from current
HEAD — materially different from "new commits added." The brief and the
`list` table need to call this out.

Solution: every snapshot stores `ordered_commit_shas` (newest-first JSON).
Diffing snapshots tells us:

- `+3 commits` — fast-forward
- `force-pushed` — `last_briefed_head_sha` not reachable from current HEAD
- `base changed` — `base_sha` changed since last snapshot

`IPrReadSource` providers must support reachability checks.

### Why stable platform IDs alongside display IDs

Repos rename. Projects move. PR numbers are repo-scoped. The pretty string
`gh.com:owner/repo#N` survives a rename only because GitHub continues to
serve at the new owner/repo via redirect — fine for HTTP, **not** fine as
a primary key.

Solution:

- `pull_requests.pr_identity` (display): primary key for commands/joins **within v0.1**
- `pull_requests.stable_identity` (platform ID-based): durable key for
  rename migrations and cross-session continuity

Both are stored from day 1. If a rename ever happens, a migration updates
`pr_identity` to the new display string while `stable_identity` stays put.

---

## Rubber-duck critique log

### Pass 1 — `gpt-5.5` rubber-duck, 2026-05-13 21:30 PDT

**Top blockers raised:**

1. **Registry state too "current-row" oriented.** ✅ Adopted — append-only
   snapshot/event tables added.
2. **Copilot handoff has a state continuity hole.** ✅ Adopted — immutable
   review-run directories + `review_runs` table.
3. **Stable PR identity underspecified for ADO.** ✅ Adopted — `stable_identity`
   column carries platform IDs (project GUID + repo GUID + PR number for ADO).
4. **Secret storage must be designed now.** ✅ Reframed by Jean-Marc — delegate
   to `gh` and `az`; no secret storage in `pr-inbox` at all.
5. **`IPrSource` cannot just be "fetch assigned PRs".** ✅ Adopted — capability-
   oriented interface with `SourceCapabilities` record; read/write separation
   enforced at type level.

**Sharp questions:**

- "Assigned to me" vs "needs my attention" → `tracking_reason` enum.
- `last_reviewed_sha` ambiguity → split into `last_briefed_head_sha` /
  `last_review_run_head_sha` / `last_posted_review_head_sha`.
- Force-push detection → ordered commit SHAs + reachability check.
- "Copilot unread" → derived query over `observed_threads`, not a stored scalar.
- v0.1 mutation gates → `IPrReadSource` only; `IPrReviewPublisher` doesn't
  exist as a type yet.

**Smaller risks flagged:**

- Sync deletion semantics → never hard-delete; mark status.
- Partial sync → per-source `sync_runs` with status enum; `list` surfaces stale sources.
- JSON arrays bad for querying → relational tables for anything counted/diffed/linked.
- Schema migrations from v0.1 with backup-before-migrate.
- Brief files may leak private data → kept under `%APPDATA%`, never in repo, gitignored.
- Git from day 1, even for personal tools.

**Things explicitly fine** (not re-litigated):

- Print-and-paste Copilot handoff for v0.1.
- SQLite as backing store.
- ADO per-project enumeration (no global reviewer inbox API).
- Stable + display identity pairing.

---

## Open decisions and ambiguities

See `AMBIGUITIES.md`.
