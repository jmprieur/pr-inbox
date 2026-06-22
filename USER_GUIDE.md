# pr-inbox — user guide

> The end-to-end "what can I do, when, and why" guide for `pr-inbox`.
> Goes deeper than the [README](README.md), which stays focused on install
> and architecture. If you're new, start with [§ First ten minutes](#first-ten-minutes).

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![Tests: 425](https://img.shields.io/badge/tests-425_passing-brightgreen)](#)

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
  - [Storage](#storage)
  - [Tokens](#tokens)
  - [What sync actually does](#what-sync-actually-does)
  - [What "Review" actually does](#what-review-actually-does)
  - [Reviews directory layout](#reviews-directory-layout)
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
| GitHub Copilot CLI (`copilot`) | Used by the launcher to run the review. Microsoft users can set `PRINBOX_REVIEW_CLI="agency copilot"`. |

### 2. Build & start

```powershell
dotnet build PrInbox.slnx
$env:ASPNETCORE_URLS = "http://localhost:7341"
dotnet run --project src/PrInbox.Web
```

Open <http://localhost:7341>.

### 3. First-run: add sources

On first run you're redirected to **Settings**. An amber banner says
"First run." and the GitHub.com identity picker is **pre-opened
automatically**. If you have one or more `gh` logins, you'll see them
listed within a few seconds:

- **+ Add all N GitHub identities** (green button, shown when ≥2 logins
  are detected) — the recommended one-click path. Adds every detected
  identity as its own source so personal + EMU sync independently from
  the start. You can remove any after the fact in the sources table.
- Click any individual login (badged `EMU` or `public`, with the
  currently-active one marked) to bind a source to just that account.
- **+ Add with default identity** — fallback for users without `gh` or
  who want a single source that tracks whichever `gh` account is
  currently active.

For GHE and ADO sources, use the **+ Add GitHub Enterprise…** and
**+ Add Azure DevOps project…** buttons below.

When you arrive on the page with no sources yet (truly fresh clone),
**Doctor runs automatically** as soon as you add the first source —
look at the per-source table for `Last sync`, `Open PRs`, and
`Identity` chips. If you have multiple `gh` logins, run
`gh auth login --hostname github.com` once per identity before opening
Settings — the picker reads from `gh auth status`.

> 💡 If you have both a default-identity source **and** an explicit
> source for your currently-active `gh` login, Doctor flags this as
> "Double-fetch" in an amber advisory **and offers a green one-click
> "Fix: Bind to `<login>`" button** that resolves it for you — both
> sources would otherwise fetch the same PRs every cycle.

### 4. First sync

Switch back to **Inbox**. If you just added sources, the inbox kicks
an out-of-band sync the moment you arrive (no waiting for the next
background tick). The first background sync takes 10–60s depending
on how many sources/PRs. The bottom-right
status line shows `Last sync: …`. PRs appear as they're discovered.

### 5. Click Review on something

Pick any row, hit **Review**. A new Windows Terminal window opens —
titled like `alice playground #8114 @ff2dcab 15:46` (author · repo · PR
number · head SHA · launch time) — runs `copilot` against the
generated brief, and starts the dual-model-review pass. **You did not
type anything in the terminal.** That's the point.

---

## Daily flow 1 — Triage the inbox

The Inbox page is your morning triage view. Pulled-down summary:

```
Source chips: [✓ EMU] [✓ public] [✓ proxima] [✓ ADO]    ← click to filter
Repos • 23 / 3 hidden ▾                                  ← click to filter
Authors • 24 / 1 hidden ▾                                ← click to filter
[  Show closed   ]   [  Show ignored  ]   [  Show done (N)  ]   [  Show only flagged (N)  ]
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
| **Repo** | `☆` star + `owner/repo` (clickable) | Click the star to flag the PR as "of interest"; star turns gold ★ |
| **#PR** | PR number + title | Click to open on the platform |
| **Author** | `@login` (avatar where available) | |
| **Age** | Days since opened | `Xd`; gets warmer over time |
| **Drift** | `+N` commits since last review, or `⚠ force-push` | Anchored on `last_reviewed_head_sha` |
| **Findings** | Per-severity pills `C:0 H:1 M:2 L:0`, plus `✓ clean` or convergence badge | Click any pill → opens Review page filtered to that severity |
| **Threads** | `N open · M bot` and (when applicable) `✓ K ready` | "ready" = K threads have a likely-done reply (see flow 3) |
| **Actions** | `Review`, `Done` / `Undo done`, `Ignore` / `Unignore` | |

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
  → drop ignored unless "Show ignored"   (disappeared PRs stay visible)
  → drop marked-done (where the author hasn't pushed since) unless "Show done"
  → drop unflagged rows when "Show only flagged" is on
```

These toggles persist per-user in SQLite (`ui_preferences` table).

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
Ignored = you (or an `IgnoredRepos` regex) said "hide this." (Disappeared
PRs are *not* ignored — they stay visible with a `no longer assigned`
chip; see [§ Disappeared PRs](#disappeared-prs).)

**Show done**: per-PR snooze. Hit the **Done** button on a row after
you've reviewed and published comments — the row hides until the
author pushes a new commit, at which point it reappears automatically
with an **`↻ updated since done`** amber chip so you know why it's
back. The toolbar label shows `Show done (N)` so you always know how
many PRs are currently snoozed. The "done" anchor is the current head
SHA at the moment you clicked Done; force-push and merge-from-base
both count as "author activity" and bring the row back. To bring a row
back manually (e.g. you marked it done by mistake), expand **Show
done** and click **Undo done**. Unlike Ignore — which is a permanent
hide — Done is a soft snooze that resolves itself on the next push.

**Flag** (the **☆ / ★** star in the Repo column): a third axis next to
Done and Ignore. Click the star on a row to mark it "of interest" —
the star turns gold and the PR joins your flagged set. Use the
**Show only flagged (N)** toolbar toggle to isolate the inbox to just
those rows. Flag is *orthogonal* to Done/Ignore/Closed: flagging a
PR does **not** bypass the other filters. A PR that's both flagged
and done stays hidden until you turn on Show done (or Show only
flagged). Use Flag when you want to keep tabs on a PR you don't need
to review and don't need to act on — to see how it lands, wait for an
author reply, or follow a teammate's work.

**Other toolbar controls:**

- **Hide drafts (N)** — collapse draft (work-in-progress) PRs.
- **Show out-of-scope** — reveal PRs hidden by a repo's monorepo path
  filter (when you've scoped a repo to specific folders).
- **Group by Tag** — group rows into collapsible sections by the tags
  you've attached.
- **Age** column header — sort oldest-first / newest-first / off.

### Disappeared PRs

When an upstream sync stops returning a PR you were tracking (someone
removed you as a reviewer, the PR was deleted, or the sweep simply
hasn't re-seen it for a while), `pr-inbox` does **not** delete the row.
Instead it stamps `disappeared_at` and:

- The row stays in the table, but is rendered muted with a
  **`no longer assigned`** chip next to the title.
- It stays **visible by default** — `Show ignored` does not gate it.
  Only an explicit Ignore (per-PR or regex) removes it from the queue.
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
   - A Phase-2 instruction to triage the existing bot threads after
     findings are written — reply / react / resolve the ones that are
     settled *and* verified (author fixed it and the agent confirmed, or
     author rejected with evidence the agent agrees with)
   - Staleness clause ("verify PR HEAD is still `<sha>` before posting")
5. Insert `review_runs` row; update `pull_requests.last_briefed_head_sha`.
6. Spawn a Windows Terminal window running `copilot …` (or `agency copilot`
   for Microsoft users), titled
   `<author> <repo> #<N> @<short-sha> <HH:mm>`. By default each review
   gets its own window; turn on **One tab per review** (Settings →
   Review launcher) to route them into one shared window as tabs instead.

A second Review on the same PR **always** creates a new immutable run —
nothing is mutated in place.

### Managing review windows

Launched reviews keep running in their own terminals. The Inbox shows a
**review consoles** strip listing every one pr-inbox is tracking, so you
don't have to dig through Alt-Tab:

- **minimize / show** an individual review window.
- **Minimize all** / **Show all** to clear or surface every review at once.

The strip acts on one OS window per review, so it's only available in the
default one-window-per-review mode. With **One tab per review** on, every
review shares a single window — which can't be minimized or focused
individually — so the strip is replaced by a short note pointing you at
the terminal's own tab bar (`Ctrl+Tab`) instead.

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
- The publisher posts the curated set when you hit **Post**. The
  **Dry run** checkbox on the Review page is on by default — the
  publisher tells you what it *would* post without making any network
  calls; uncheck it to post for real. You can also pick a GitHub review
  verdict (**Comment** / **Approve** / **Request changes**).
- **Open findings.yaml** edits the raw run file in your default editor;
  saving refreshes the page. **✓ Mark done & back** posts nothing but
  snoozes the PR and returns you to the Inbox.

Per-finding idempotency: posting the same finding twice is a no-op.
If you re-post after editing, the publisher posts the diff.

> **You own what you publish.** A standing notice above the **Post**
> controls is a reminder that the review is AI-assisted but the comments
> are yours — read each finding before posting. Posting for real also
> asks you to confirm you're accountable for the AI-assisted comments.

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

"Ready" means: an open thread's **latest** reply matches a conservative
"done / fixed / addressed / +1" pattern. Click the pill to open the
Threads page filtered to that PR.

### The Threads page

`/threads?url=<pr-url>` shows every open review thread on the
PR, one row per thread, with:

| Column | |
|---|---|
| ☐ | Per-row checkbox to pick threads to resolve |
| Author | Who started the thread |
| Anchor | File path + line (e.g. `src/foo.cs:42`) |
| Excerpt | Last comment body |
| State | `open · Xd ago` and (when applicable) a `✓ done` badge |

Bulk selectors at the top — **Copilot**, **All bots**, **All**, **Clear** —
pick threads fast, and a **Refresh thread ids** button backfills any
missing GraphQL node ids.

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
| (hidden) | Zero findings — `✓ clean` shows instead; or only one reviewer ran |
| `✓✓` converged (green) | Both reviewers flagged **every** finding — highest confidence |
| `⚠` asymmetric (amber) | At least one finding came from a single reviewer; worth a second look |

Tooltip shows the count delta and the names of the models involved.

### `✓ clean` vs no findings at all

`✓ clean` appears when a review **ran** and produced zero findings. An
unreviewed PR shows no findings cell content at all — don't read missing
content as "clean." When two reviewers both came back empty, the pill's
tooltip says so (e.g. "2 reviewers agree").

### Bot-comment count

`N bot` in the Threads column counts comments from Copilot review,
Copilot coding agent, and any logins you added to `bots.extraLogins`
in config. Useful for distinguishing "5 open threads I started" from
"5 open threads the bot started."

### `no longer assigned` chip

A muted row with this chip means the upstream sync has stopped returning
the PR (you got removed from reviewers, the PR was deleted, you're no
longer assigned, etc.). The data is still there — `pr-inbox` never
hard-deletes — and the row **stays visible** so you don't lose track of
it. Click through to verify on the platform; if it really is gone, hit
**Ignore** to demote it permanently.

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

Auto-runs the first time you open Settings with at least one source
configured; otherwise click **Run Doctor**. The report has two parts:

1. **Advisories** — pattern-detection across your config. Today the
   shipped patterns are:
   - **Double-fetch** (amber): a default-identity gh.com source
     coexists with an explicit-identity source bound to the currently-
     active `gh` login. Both fetch the same PRs; click the green
     **Fix: Bind `<id>` to `<login>`** button to migrate the default
     source in one click — it removes the redundant default and (since
     the explicit one already covers that login) leaves you with a
     clean per-identity setup.
   - **Last sync failed** (amber): the most recent sync for a source
     ended with an error. Shows the source id and error message, plus
     a **Retry sync** button that nudges the sync loop and re-runs
     Doctor a few seconds later.
   - **Missing `gh` scopes** (amber): the token bound to a github.com
     login is missing one of the scopes pr-inbox relies on (`repo`,
     `read:org`). Surfaces the host + login + missing scopes and a
     copy-pasteable `gh auth refresh -h <host> -s <scopes>` command.
     No button — the refresh flow is interactive (device-code), so you
     run it yourself in a terminal.
   - **Rate-limit headroom low** (info): the core GitHub/GHE rate-limit
     for a host has dropped below 15% remaining for the hour. Just an
     FYI — sync will keep working, but if you trigger a big enrichment
     burst right now it may stall until the next reset window.
2. **Per-source table** — ID, Kind, Identity (with `EMU` / `public` /
   `active` chips), Auth status (token length / az identity), **Last
   sync** (relative time), and **Open PRs** count. The runtime columns
   are the fastest way to spot a source that's silently failing — an
   "Auth OK" row with `Last sync = never` after the app's been running
   a few minutes is a sync-loop problem, not an auth problem.

The runtime columns shell out to `gh auth status` and read SQLite for
last-sync timestamps and open-PR counts; expect ~1-2s on a normal run.

### Ignored repos (regex list)

One regex per line. Matches against `DisplayRepo` (e.g.
`owner/repo` or `org/project/repo` for ADO). Case-insensitive.
Invalid patterns are dropped with a warning, never crash the page.
Use for "everything from this team gets hidden by default."

### Review launcher

Persisted settings that take effect on the **next** review you launch
(running reviews are unaffected):

| Setting | Effect |
|---|---|
| **AutoSend** | After spawning the terminal, hands the brief to the agent (`-i`) so the run starts hands-free. When off, the brief is copied to the clipboard and the terminal waits for you to paste it (Ctrl+V). |
| **Yolo** | Appends `--yolo` to the review CLI invocation (`--allow-all-tools --allow-all-paths --allow-all-urls`), skipping every permission prompt. Faster and truly unattended — use only when you trust the agent. |
| **Tab colour** | Colours the Windows Terminal tab for every review so it stands out from ordinary terminals. Accepts a hex like `#5da4ff`; leave blank to disable. |
| **One tab per review** *(experimental)* | On: each review opens as a tab in one shared window (`pr-inbox-reviews`) instead of its own window — less desktop clutter when several run at once. Trade-off: the Inbox's per-review window controls don't apply in tab mode, and closing the shared window closes every review tab. Off (default): one window per review. |

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
pr-inbox config add-source github github.com
pr-inbox config add-ado-project mseng Context
pr-inbox config doctor
pr-inbox sync
pr-inbox list
pr-inbox review https://github.com/owner/repo/pull/1234
```

`pr-inbox review` takes a full PR **URL** (e.g.
`https://github.com/owner/repo/pull/1234`, or an Azure DevOps PR URL),
not the short display id the Inbox shows. Add `--refresh` to re-sync the
PR first. Other handy flags: `sync --fast` / `--enrich` / `--source <id>`
and `list --all` / `--source <id>`.

---

## Under the hood

If something feels off and you want to verify what `pr-inbox` thinks
about your PRs, you can poke at the local DB directly.

### Storage

`%APPDATA%\PrInbox\pr-inbox.db` (SQLite). Key tables:

| Table | Holds |
|---|---|
| `pull_requests` | Current row per PR (latest snapshot summary + cached counts) |
| `pr_source_bindings` | `(stable_id, source_id, identity)` — which sources observed each PR; lets two same-host identities track the same PR independently |
| `pr_snapshots` | Append-only per-sync snapshot of each PR's state |
| `observed_threads` | Append-only per-comment row — replies share `platform_thread_node_id` |
| `review_runs` | One row per Review click; immutable |
| `posted_reviews` | One row per published finding; idempotency key |
| `ui_preferences` | The Web UI's toggle state (source chips, denylists, AutoSend/Yolo, etc.) |
| `sync_runs` | One row per sync attempt; status, duration, source breakdown |

Nothing is hard-deleted. "Ignore" sets a flag. "Resolve thread" updates
the upstream platform and re-syncs.

### Tokens

Never stored by `pr-inbox` — fetched on demand from `gh` / `az`. Per source:

| Source | How a token shows up |
|---|---|
| GitHub.com (default identity) | `gh auth token --hostname github.com` (subprocess) |
| GitHub.com (explicit identity) | `gh auth token --hostname github.com --user <login>` |
| GitHub Enterprise | `gh auth token --hostname <ghe-host>` (with `--user <login>` for explicit identity) |
| Azure DevOps | `Azure.Identity.AzureCliCredential` (resource `499b84ac-1321-427f-aa17-267ca6975798`) |

So: log in / refresh those CLIs and `pr-inbox` follows along
automatically. When a source is bound to an explicit identity, the
token provider pins `gh` to that login — so two same-host sources
(e.g. personal + EMU) fetch with the correct credentials
independently.

### What sync actually does

```
Inbox sync (background)
  every 5 min (default; set PrInbox:SyncIntervalSeconds to change)
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

### What "Review" actually does

Whether you click **Review** in the Web UI or run `pr-inbox review <id>`
from the CLI, the same seven steps run:

1. Single-PR fast-path `sync` — refreshes that one PR's snapshot before
   anything else, so the brief reflects the current upstream state.
2. Computes what's new since `last_briefed_head_sha` — commits,
   force-push, base change, new threads, new bot comments,
   newly-resolved threads.
3. Creates an **immutable** run directory under
   `%APPDATA%\PrInbox\reviews\<pr_dir>\<UTC-ts>_<head_sha[:12]>\`.
4. Writes `brief.md` containing:
   - PR identity + URLs + author + title
   - Head/base SHAs, last-briefed / last-reviewed / last-posted SHAs
   - Diff summary since last brief (commits, force-push, base change)
   - Embedded unified diff up to 50 KB; beyond that, file list + diff URL
   - Your open threads with status
   - Recent bot comments (Copilot review, Copilot coding agent) since
     the last brief
   - Standard `dual-model-review` invocation block (Opus 4.7 + GPT-5.5,
     asymmetry instructions, `do NOT post`, `diff_anchorable` flag,
     95%+ inline filter)
   - Staleness clause ("verify PR HEAD is still `<sha>` before posting")
5. Writes `metadata.json` (machine-readable mirror of the brief).
6. Inserts a `review_runs` row and updates
   `pull_requests.last_briefed_head_sha`.
7. Prints the brief path and the recommended `copilot` command (CLI) or
   spawns `wt.exe` running `copilot` (or `agency copilot`) against the brief (Web UI),
   in a window titled `<author> <repo> #<PR-number> @<short-sha> <HH:mm>`
   (or a tab in the shared `pr-inbox-reviews` window when **One tab per
   review** is enabled).

Re-running on the same PR **always** creates a new immutable run —
nothing is overwritten. The previous run dir stays exactly as it was.

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
| Review tab opens then exits ("copilot: command not found") | Review CLI not on `PATH` | Install the GitHub Copilot CLI, or set `PRINBOX_REVIEW_CLI` (Microsoft: `agency copilot`) |
| Review tab opens but plugin fetch fails | No access to the configured plugin source | Set `PRINBOX_REVIEW_PLUGIN=local:<path>` (see [README § overrides](README.md#review-launcher-overrides)) |
| Review tab opens but model call fails | Review CLI not authenticated to the chosen model | Authenticate it, or change `PRINBOX_REVIEW_MODEL` |
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
