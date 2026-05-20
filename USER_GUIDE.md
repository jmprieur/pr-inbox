# pr-inbox — user guide

> The end-to-end "what can I do, when, and why" guide for `pr-inbox`.
> Goes deeper than the [README](README.md), which stays focused on install
> and architecture. If you're new, start with [§ First ten minutes](#first-ten-minutes).

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![Tests: 360](https://img.shields.io/badge/tests-360_passing-brightgreen)](#)

---

## Table of contents

- [Mental model in one paragraph](#mental-model-in-one-paragraph)
- [First ten minutes](#first-ten-minutes)
- [Daily flow 1 — Triage the inbox](#daily-flow-1--triage-the-inbox)
  - [Disappeared PRs](#disappeared-prs)
- [Daily flow 2 — Review a PR](#daily-flow-2--review-a-pr)
- [Daily flow 3 — Close the loop](#daily-flow-3--close-the-loop)
- [When something looks off](#when-something-looks-off)
- [Settings tour](#settings-tour)
- [The CLI, briefly](#the-cli-briefly)
- [Under the hood](#under-the-hood)
- [Troubleshooting](#troubleshooting)
- [Glossary](#glossary)

---

## Mental model in one paragraph

`pr-inbox` is a personal harness for reviewing many PRs at scale across
**GitHub.com**, **GitHub Enterprise**, and **Azure DevOps**. It does not
review code — `dual-model-review` (Opus + GPT) does that. `pr-inbox`'s job
is to tell you **which** PRs to look at, **what changed** since you last
looked, hand a fully-bootstrapped brief to a Copilot tab, and remember
what you did. Two surfaces: the **Web UI** (Blazor, the daily driver) and
the **CLI** (`pr-inbox`, for scripting/automation). Both read and write
the same local SQLite at `%APPDATA%\PrInbox\pr-inbox.db`. No tokens are
stored anywhere — they're minted on demand from `gh` and `az`.

---

## First ten minutes

The fastest path from clone to "Review" button works against real PRs.

### 1. Prereqs (one-time)

You need these on `PATH`:

| Tool | Why |
|---|---|
| .NET 10 SDK | Build + run |
| `gh` (GitHub CLI) | GitHub auth — `gh auth login --hostname github.com` |
| `az` (Azure CLI) | ADO auth — `az login` (skip if no ADO sources) |
| `pwsh` (PowerShell 7+) | Review launcher runs under this |
| `wt.exe` (Windows Terminal) | Each Review opens in a new tab |
| `agency` CLI | Used by the launcher to spawn `agency copilot …` |

### 2. Build & start

```powershell
dotnet build PrInbox.slnx
$env:ASPNETCORE_URLS = "http://localhost:7341"
dotnet run --project src/PrInbox.Web
```

Open <http://localhost:7341>.

### 3. First-run: add sources

On first run you're redirected to **Settings**. The banner says
"First run." Click:

- **+ Add GitHub.com** — if `gh` is installed and you have one or
  more logged-in accounts, an inline picker lists each (badged
  `EMU` or `public`, with the active one marked). Click a login
  to bind a source to that exact account. Have two GitHub
  identities (e.g. a personal `jenny` + an enterprise
  `jenny_microsoft`)? Add each as its own source — they sync
  independently and the **EMU** / **public** chips filter them
  apart in the Inbox. A **+ Add with default identity** fallback
  is always offered for users without `gh`, or who want the
  source to track whichever `gh` account is currently active.
- **+ Add GitHub Enterprise…** — enter your GHE host (e.g. `github.contoso.com`).
- **+ Add Azure DevOps project…** — `org` + `project`, one row per project.

Then click **Run Doctor**. Green checks mean you're authenticated; red
means you need to `gh auth login` / `az login` and re-run. If you have
multiple `gh` logins, run `gh auth login --hostname github.com` once
per identity before opening Settings — the picker reads from `gh auth
status`.

### 4. First sync

Go back to **Inbox**. The first background sync runs immediately and
takes 10–60s depending on how many sources/PRs. The bottom-right
status line shows `Last sync: …`. PRs appear as they're discovered.

### 5. Click Review on something

Pick any row, hit **Review**. A new Windows Terminal tab opens titled
`<repo> #<N>`, runs `agency copilot` against the generated brief, and
starts the dual-model-review pass. **You did not type anything in the
terminal.** That's the point.

---

## Daily flow 1 — Triage the inbox

The Inbox page is your morning triage view. Pulled-down summary:

```
Source chips: [✓ EMU] [✓ public] [✓ proxima] [✓ ADO]    ← click to filter
Repos • 23 / 3 hidden ▾                                  ← click to filter
Authors • 24 / 1 hidden ▾                                ← click to filter
[  Show closed   ]   [  Show ignored  ]
[ Refresh now ]   12 shown · 47 total      Last sync: 2026-05-18 13:42
```

The number on each filter pill is **honest**: `visible / hidden` only
counts groups that actually have at least one matching row, so stale
exclusions (still in the denylist but no longer matching anything)
don't inflate the tally. When nothing is hidden the pill collapses to
just `Repos • 23` / `Authors • 24`.

### Reading a row

| Column | What it shows | Notes |
|---|---|---|
| **Repo** | `owner/repo` (clickable) | EMU/public/GHE/ADO color-coded |
| **#PR** | PR number + title | Click to open on the platform |
| **Author** | `@login` (avatar where available) | |
| **Age** | Days since opened | `Xd`; gets warmer over time |
| **Drift** | `+N` commits since last review, or `⚠ force-push` | Anchored on `last_reviewed_head_sha` |
| **Findings** | Per-severity pills `C:0 H:1 M:2 L:0`, plus `✓ clean` or convergence badge | Click any pill → opens Review page filtered to that severity |
| **Threads** | `N open · M bot` and (when applicable) `✓ K ready` | "ready" = K threads have a likely-done reply (see flow 3) |
| **Actions** | `Review`, `Ignore` / `Unignore` | |

Rows you used to track but are no longer assigned to appear muted with
a small **`no longer assigned`** chip — see [§ Disappeared PRs](#disappeared-prs)
below.

### Filters you'll actually use

The pipeline applied to every load:

```
all PRs
  → drop closed unless "Show closed"
  → drop sources not in your chip set
  → drop repos in your repo denylist
  → drop authors in your author denylist
  → drop ignored / disappeared unless "Show ignored"
```

All four filters persist per-user in SQLite (`ui_preferences` table).

**Repo filter** (the **Repos** pill): click it, search-as-you-type,
check/uncheck. Default: every repo visible. Excluded repos still appear
in the popover (with count 0 if no current rows) so you can always
re-enable them. Two action buttons live above the list:

- **Show all** — clear the denylist.
- **Hide visible** — hide everything currently shown in the popover. If
  you've typed `bot` in the search box, one click hides every matching
  bot repo. Disabled when there's nothing to hide.

A second toolbar lets you flip the sort:

- **PRs** (default) — highest count first, then alphabetic.
- **Recent** — repos with the most recent upstream activity first (using
  the PR's upstream "updated at" timestamp). Repos with no observed
  activity yet are demoted to the bottom. Your choice is persisted
  per popover, so you can have `Repos sorted by Recent` and
  `Authors sorted by PRs` at the same time.

**Author filter** (the **Authors** pill): identical shape to Repos.
Useful when one team's PRs are dominating the inbox and you want to
skim without losing them. Authors with missing logins are bucketed as
`(unknown)`.

**Source chips**: top-level cohort filter — EMU, public, GHE (proxima),
ADO. Unchecking a chip is a faster gesture than ignoring every repo on
that platform.

**Show closed / Show ignored**: hidden by default to keep the inbox
focused on actionable PRs. Closed = PR is merged/closed upstream.
Ignored = you (or an `IgnoredRepos` regex) said "hide this," or the PR
was reported as disappeared by the sweep.

### Disappeared PRs

When an upstream sync stops returning a PR you were tracking (someone
removed you as a reviewer, the PR was deleted, or the sweep simply
hasn't re-seen it for a while), `pr-inbox` does **not** delete the row.
Instead it stamps `disappeared_at` and:

- The row stays in the table, but is rendered muted with a
  **`no longer assigned`** chip next to the title.
- The **Show ignored** toggle controls visibility (off by default).
- The PR can still be opened, reviewed, and un-ignored manually — the
  data is intact, just demoted from your active queue.

This protects you from losing context when a PR drops off temporarily
(e.g. you got removed from reviewers, then re-added). If you decide
it's truly gone, ignoring it is one click.

### What if a repo I want to ignore isn't in the popover?

Add a regex under **Settings → Ignored repos**:

```
^contoso/sandbox-.*
```

Anything matching gets hidden whenever **Show ignored** is off, and the
match is reported in the row's tooltip when on. Regexes are
case-insensitive and `RegexOptions.Compiled`. Bad regexes are logged and
skipped — the page never crashes over config.

---

## Daily flow 2 — Review a PR

### Hitting "Review"

`pr-inbox` does the following before the terminal even opens:

1. Fast-path **sync** for just that PR — refresh snapshot, base/head, etc.
2. Compute **what's new** since `last_briefed_head_sha`:
   commits, force-push, base change, new threads, new bot comments,
   newly-resolved threads.
3. Create an **immutable** run directory:
   `%APPDATA%\PrInbox\reviews\<pr_dir>\<UTC-ts>_<head_sha[:12]>\`
4. Write `brief.md`:
   - PR identity + URLs + author + title
   - Head/base SHAs + last-reviewed/posted/briefed deltas
   - Unified diff up to 50KB (file list + URL beyond that)
   - Your open threads
   - Recent bot comments (Copilot review, Copilot coding agent)
   - The standard dual-model-review invocation block
   - Staleness clause ("verify PR HEAD is still `<sha>` before posting")
5. Insert `review_runs` row; update `pull_requests.last_briefed_head_sha`.
6. Spawn a new Windows Terminal tab running `agency copilot …`,
   titled `<repo> #<N>`.

A second Review on the same PR **always** creates a new immutable run —
nothing is mutated in place.

### Reading the Review page

After the review runs, the brief and its findings live in the page at
`/review?url=<pr-url>`. You'll see:

- **Header**: PR identity, head SHA, timestamps. If the upstream PR has
  moved past the SHA the review was anchored on, a **HEAD-drift chip**
  flags it (`⚠ stale: HEAD is now <new>`).
- **Verdict combo**: high-level signal at the top
  (`✓ clean` / convergence badge / asymmetric / counts per severity).
- **Convergence callout**: when both models converged, an inline pill
  near the run summary makes it explicit ("Both models converged on
  this set"). Amber callout for asymmetric runs (one model found
  things the other missed).
- **Findings table**: severity, file:line, title, both-models-flagged?,
  publisher state.

### Editing & posting findings

Each finding row is editable inline:

- Toggle a finding's **publish?** flag (skip false-positives without
  deleting the record).
- Edit the comment body. The publisher uses your edited copy, not the
  original.
- The publisher posts the curated set when you hit **Post**. Default is
  **dry-run** (publisher tells you what it would post, doesn't actually
  do it). Flip dry-run off in `findings.yaml` (or the page toggle, where
  exposed) to post for real.

Per-finding idempotency: posting the same finding twice is a no-op.
If you re-post after editing, the publisher posts the diff.

---

## Daily flow 3 — Close the loop

After someone replies "Done in `abc1234…`" to your review, you want
the thread off your screen without manually clicking around. Two
features make this easy.

### The "ready" pill on the inbox

When threads on a PR have likely-done replies, a green pill appears in
the Threads column:

```
12 open · 3 bot   [✓ 2 ready]   ← anchor; click to jump to /threads
```

"Ready" means: among threads where you have a comment, the **latest**
reply matches a conservative "done/fixed/+1" pattern. Click the pill
to open the Threads page filtered to that PR.

### The Threads page

`/threads?identity=<pr-identity>` shows every open review thread on the
PR, one row per thread, with:

| Column | |
|---|---|
| Path | File path + line range |
| Body | Last comment (yours or theirs); markdown-rendered |
| State | `open · Xd ago` and (when applicable) `✓ done` badge |
| Resolve | Per-row checkbox |

When the "done" heuristic fires on the latest reply, you'll see a
**✓ Done replies (N)** bulk button at the top — one click resolves
every thread whose latest reply looks like a "done."

The heuristic is intentionally conservative — false-negatives (you have
to resolve manually) are cheap; false-positives (auto-resolved while
the conversation is still live) are annoying. What it matches:

- Bare verbs at the start of the body: `done`, `fixed`, `resolved`,
  `addressed`, `acknowledged` (with sentence boundary or `in <token>`)
- `Done in e0193224`, `Fixed in #123`
- Standalone `+1`

What it deliberately does **not** match:

- "Done thoroughly checked…" (no boundary after the verb)
- "Not done yet" / "Won't fix"
- `+1 but only if…`

Tune cases by adding `[InlineData(...)]` rows to
`tests/PrInbox.Tests/Web/DoneReplyHeuristicTests.cs`.

### Dry-run, then post

The Threads page defaults to **dry-run** for safety. The flow is:

1. Pick threads to resolve (per-row or via the bulk button).
2. With the **Dry-run plan** badge lit, click **Plan resolve (N)** — the
   page tells you which API calls it would make.
3. Toggle dry-run off — the button label flips to **Resolve N
   thread(s)**. Click it and the calls go through (you'll get a
   confirmation prompt — "This cannot be undone.").
4. The resolved threads disappear from the page (and stop driving the
   "ready" pill on the inbox).

Thread resolution lives in the GitHub-side flow: GitHub uses the GraphQL
`resolveReviewThread` mutation. **ADO resolve is not yet supported from
inside `pr-inbox`** — the orchestrator surfaces a friendly failure that
points you to the PR page to change the status manually.

---

## When something looks off

A short tour of the cues built into the UI so you know when to trust
the signal and when to dig deeper.

### Drift chip — `+5` or `⚠ force-push`

On an Inbox row, this means **the PR has moved since the last review
you anchored**. Click it to open the GitHub/ADO compare view between
`last_reviewed_head_sha` and current HEAD.

- `+N` — N new commits, fast-forward only.
- `⚠ force-push` — old HEAD is no longer in the branch history. Treat
  the previous review as stale; re-review from scratch.

### HEAD-drift chip on Review page

Same idea but inside an already-open Review. If you opened a Review
and the PR has moved since the run started, the chip warns you before
you post. The publisher won't refuse to post on drift — it's your call
— but the chip + brief's staleness clause make sure you make it
consciously.

### Convergence badge

On the Inbox findings cell:

| Badge | Meaning |
|---|---|
| (hidden) | Both findings sets are empty — `✓ clean` shows instead |
| ⇆ converged (green) | Both models flagged the same set within tolerance |
| ⚠ asymmetric (amber) | One model found materially more than the other; worth a second look |

Tooltip shows the count delta and the names of the models involved.

### `✓ clean` vs no findings at all

`✓ clean` only appears when the review **ran** and produced an empty
findings set (both models agreed there was nothing to flag). An
unreviewed PR shows no findings cell content at all. Don't read missing
content as "clean."

### Bot-comment count

`N bot` in the Threads column counts comments from Copilot review,
Copilot coding agent, and any logins you added to `bots.extraLogins`
in config. Useful for distinguishing "5 open threads I started" from
"5 open threads the bot started."

### `no longer assigned` chip

A muted row with this chip means the upstream sync has stopped returning
the PR (you got removed from reviewers, the PR was deleted, you're no
longer assigned, etc.). The data is still there — `pr-inbox` never
hard-deletes — and **Show ignored** brings it back into view. Click
through to verify on the platform; if it really is gone, hit **Ignore**
to demote it permanently.

This is the safety net for "wait, where did that PR go?" — a question
that used to require searching the platform manually.

---

## Settings tour

Six sections, top to bottom.

### Sources

Add/remove sources here. One row per source, showing kind, host,
identity (the `gh` login or `"default"` for active-account
semantics), and an "enabled" flag (toggled via the CLI today — UI
is add/remove only). **+ Add GitHub.com** opens an inline picker
that probes `gh auth status --hostname github.com` and offers one
button per logged-in account (badged `EMU` or `public`, with the
active one marked). Pick the one you want and a source bound to
that identity is added. A **+ Add with default identity** fallback
covers the no-`gh` and "use whichever account is active" cases.
GHE still uses a simple host + optional custom id form.

A heads-up banner appears in the picker if you already have a
default-identity `github.com` source AND the active `gh` login is
shown unbound — adding it explicitly would duplicate sync rows;
remove the default-identity source first if you want a clean
per-identity setup.

### Azure DevOps projects

One row per `org/project`. Pulls PRs assigned to your identity (the
one `az` is logged in as) across every repo in the project. Identity
is resolved at sync time via `Azure.Identity.AzureCliCredential`.

### Doctor

Click **Run Doctor** to verify auth across every configured source.
Reports the identity it sees for each — useful when the inbox seems
empty (often: `gh` identity doesn't match the PR assignee).

### Ignored repos (regex list)

One regex per line. Matches against `DisplayRepo` (e.g.
`owner/repo` or `org/project/repo` for ADO). Case-insensitive.
Invalid patterns are dropped with a warning, never crash the page.
Use for "everything from this team gets hidden by default."

### Review launcher

Two persisted toggles:

| Toggle | Effect |
|---|---|
| **AutoSend** | After spawning the terminal, types the prompt + Enter automatically. When off, the terminal opens at the prompt and waits for you. |
| **Yolo** | Appends `--yolo` to the `agency copilot` invocation, skipping safety prompts. Use with care. |

If you need fancier overrides (different model, different plugin),
use the env vars in [§ Review launcher overrides](README.md#review-launcher-overrides).

### Where things live

The Settings page prints the config file path at the top. The other
paths follow the same `%APPDATA%\PrInbox\` convention:

- Config file: `%APPDATA%\PrInbox\config.json`
- Database: `%APPDATA%\PrInbox\pr-inbox.db`
- Reviews directory: `%APPDATA%\PrInbox\reviews\`
- Logs directory: `%APPDATA%\PrInbox\logs\`

A note at the bottom of Settings reminds you that source add/remove
takes effect only after the next app restart; toggles like ignored
repos and launcher flags apply immediately.

---

## The CLI, briefly

The CLI is feature-equivalent to the Web UI for everything except the
"open a terminal tab" flow. You'd use it for:

- Scripted sync (`pr-inbox sync` in a scheduled task)
- Quick triage when you don't want a browser tab
- CI/automation against the same SQLite registry

```powershell
pr-inbox config init                           # one-time
pr-inbox config add-source github.com
pr-inbox config add-ado-project mseng Context
pr-inbox config doctor
pr-inbox sync
pr-inbox list
pr-inbox review gh.com:owner/repo#1234
```

Identity format: `<source>:<owner-or-project>/<repo>#<N>`. The display
ids the Web UI shows are accepted by `pr-inbox review`.

---

## Under the hood

If something feels off and you want to verify what `pr-inbox` thinks
about your PRs, you can poke at the local DB directly.

### Storage

`%APPDATA%\PrInbox\pr-inbox.db` (SQLite). Key tables:

| Table | Holds |
|---|---|
| `pull_requests` | Current row per PR (latest snapshot summary + cached counts) |
| `pr_snapshots` | Append-only per-sync snapshot of each PR's state |
| `observed_threads` | Append-only per-comment row — replies share `platform_thread_node_id` |
| `review_runs` | One row per Review click; immutable |
| `posted_reviews` | One row per published finding; idempotency key |
| `ui_preferences` | The Web UI's toggle state (source chips, denylists, AutoSend/Yolo, etc.) |
| `sync_runs` | One row per sync attempt; status, duration, source breakdown |

Nothing is hard-deleted. "Ignore" sets a flag. "Resolve thread" updates
the upstream platform and re-syncs.

### Tokens

Never stored. Per source:

| Source | How a token shows up |
|---|---|
| GitHub.com | `gh auth token --hostname github.com` (subprocess) |
| GitHub Enterprise | `gh auth token --hostname <ghe-host>` |
| Azure DevOps | `Azure.Identity.AzureCliCredential` (resource `499b84ac-1321-427f-aa17-267ca6975798`) |

So: log in / refresh those CLIs and `pr-inbox` follows along
automatically.

### What sync actually does

```
Inbox sync (background)
  every ~30s
    └── fast pass — pull each enabled source's "PRs assigned to me"
                    diff against pull_requests; upsert; emit changes
    └── enrich   — for PRs that need it: fetch threads, bot comments,
                    head SHA; snapshot
    └── sweep    — Option-C dual sweep:
                    a) re-fetch any PR not seen this cycle → mark
                       disappeared_at if upstream-closed
                    b) TTL re-enrich PRs older than the freshness budget
```

You can re-trigger immediately with **Refresh now** on the inbox.

### Reviews directory layout

```
%APPDATA%\PrInbox\reviews\
  └── gh.com_owner_repo_1234\
        ├── 2026-05-18T13-42-07Z_a1b2c3d4e5f6\
        │     ├── brief.md
        │     ├── metadata.json
        │     └── findings.yaml         (after first run)
        └── 2026-05-19T09-11-44Z_f6e5d4c3b2a1\
              └── …
```

Each timestamped subdir is **immutable**. Re-reviewing the same PR
appends a new subdir; nothing is overwritten.

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| `config doctor` red on GitHub | Not signed in to `gh` | `gh auth login --hostname github.com` |
| `config doctor` red on ADO | `az` token expired, or no ADO sources configured | `az login`, or just skip the ADO step |
| Sync runs but inbox empty | `gh` identity ≠ PR assignee | Compare what Doctor prints with the assignee on a known PR |
| Inbox row missing for a PR you expect | Hidden by an `IgnoredRepos` regex, repo denylist, author denylist, or source chip | Toggle **Show ignored** + check each filter pill's status line |
| Review tab opens then exits ("agency: command not found") | `agency` CLI not on `PATH` | Install agency, or override `PRINBOX_REVIEW_AGENT` |
| Review tab opens but plugin fetch fails | No access to `1ES-microsoft/ai-plugins` | Set `PRINBOX_REVIEW_PLUGIN=local:<path>` (see [README § overrides](README.md#review-launcher-overrides)) |
| Review tab opens but model call fails | `agency` not authenticated to the chosen model | Authenticate it, or change `PRINBOX_REVIEW_MODEL` |
| Web says "port already in use" | Stale Kestrel still listening | `Get-NetTCPConnection -LocalPort 7341 \| %{ Stop-Process -Id $_.OwningProcess -Force }` |
| Build fails with `MSB3027`/`MSB3021` (dll locked) | The running Web UI holds the dll lock | Stop it first: find PID on port 7341, `Stop-Process -Id <pid> -Force`, then rebuild |
| "✓ N ready" pill on a PR but Threads page shows no done badge | The thread's latest reply just dropped in — caches are eventually consistent; **Refresh now** reconciles | |
| Done badge on a thread that's NOT actually done | False positive on the heuristic | Add a test case to `DoneReplyHeuristicTests.cs` with the offending body and tune the regex; PRs welcome |

Logs: `%APPDATA%\PrInbox\logs\pr-inbox-*.log` (rolling daily).

---

## Glossary

| Term | Meaning |
|---|---|
| **Source** | A configured PR backend: GitHub.com, GHE, or an ADO project |
| **Identity** | The user account on a source. Default = whatever `gh`/`az` is logged in as |
| **Display id** | Human-readable PR ref: `gh.com:owner/repo#1234`, `ado:org/project/repo#42` |
| **Stable id** | Repo-id + PR-id form that survives renames |
| **Brief** | Immutable `brief.md` written into the run dir; what the model reads |
| **Run** | One Review click → one timestamped subdir under `reviews\` |
| **Findings** | Curated set the publisher will (or did) post |
| **Convergence** | Both models flagged the same set within tolerance |
| **Asymmetry** | One model found materially more than the other |
| **Drift** | The PR moved since the run was anchored (new commits or force-push) |
| **Ready** | A thread whose latest reply matches the "done/fixed" heuristic |
| **Ignore** | Per-PR hide flag; does not delete |
| **Disappeared** | A previously-tracked PR no longer returned by upstream — surfaced with a `no longer assigned` chip |
| **Hide visible** | One-click button on filter popovers that hides everything currently shown (respects the search box) |
| **dry-run** | The publisher tells you what it would do, without making API calls |

---

## See also

- [README.md](README.md) — install, architecture, launcher overrides
- [ARCHITECTURE.md](ARCHITECTURE.md) — design rationale + critique log
- [AMBIGUITIES.md](AMBIGUITIES.md) — open design decisions
