# Ambiguities and open questions

*Read this first on wake-up. The list grows as I build — each item is something I
either decided with a default (clearly named) or could not decide without you.
Defaults are honest best-guesses, **not** silent assumptions.*

*Format: each item has a status (✅ resolved / ⚠️ default-applied / ❓ needs you),
a default I'm working with, and the reasoning.*

---

## Resolved before bed (you answered)

- ✅ **Target framework** — .NET 10 (your machine has 10.0.202 installed; not preview)
- ✅ **License** — MIT
- ✅ **Commit attribution** — author = you (legal accountability); `Co-authored-by: Copilot` trailer per framework rules
- ✅ **Credentials** — delegate to `gh` + `az`; `pr-inbox` stores no tokens
- ✅ **ADO seed project** — `mseng/Context` (the repo you worked in on 2026-05-13)
- ✅ **Brief diff inclusion** — embed unified diff up to 50KB; beyond that, file list + URL
- ✅ **Known bots** — seed `Copilot` plus my hardcoded defaults
- ✅ **GHE hostname** — placeholder in config; you fill it in via `pr-inbox config add-source ghe.<host>`
- ✅ **Review handoff** — print-and-paste only in v0.1; `--launch` flag deferred to v0.2
- ✅ **Snapshot retention** — keep forever; explicit `clear`/archive command later

---

## Default-applied (no decision needed from you, just naming the choice)

### Tooling and packaging

- ⚠️ **Solution format** — `.slnx` (the new .NET 10 XML solution file). `dotnet new sln` in .NET 10 produces this by default; tooling supports it everywhere I checked.
- ⚠️ **NuGet sources** — added a local `NuGet.config` mapping all packages to `nuget.org` only. Restore was failing with `NU1507` because your machine has both `nuget.org` and `IDDP` (Microsoft internal feed) configured and CPM requires explicit source mapping. Local config keeps this build deterministic. If you want IDDP available for internal packages later, we'll add explicit mappings then.
- ⚠️ **`PackageId`** — `JmPrieur.PrInbox`. If you publish to NuGet someday, this is the id. Easy to rename pre-publish.
- ⚠️ **`ToolCommandName`** — `pr-inbox`. The shipped binary will install as `pr-inbox` on PATH.
- ⚠️ **`AssemblyName`** — `pr-inbox` (same as the tool command). Otherwise `PrInbox.Cli` shows up in error stack traces.
- ⚠️ **Version** — `0.1.0-alpha`. Will bump as features land.

### Language and conventions

- ⚠️ **C# version** — `latest` (currently C# 13). Nullable enabled, implicit usings on, file-scoped namespaces.
- ⚠️ **Warnings as errors** — yes, with `NU1701` (downgrade target framework warnings — Octokit ships netstandard2.0 binaries) and `CS1591` (missing XML doc) excluded.
- ⚠️ **Async everywhere** — including SQLite (`Microsoft.Data.Sqlite` supports it).
- ⚠️ **Cancellation tokens** — propagated through every async call.

### Libraries chosen

- ⚠️ **CLI parsing + UX** — `Spectre.Console.Cli` + `Spectre.Console`. One package, beautiful tables for `list`, idiomatic command app pattern. Alternative was `System.CommandLine` (still beta after years); Spectre is more mature and the table rendering is exactly what `list` needs.
- ⚠️ **Storage** — `Microsoft.Data.Sqlite` 10.0.0. Handwritten SQL migrations as embedded resources (no EF Core, no FluentMigrator — for a personal tool, grep-able SQL is more honest).
- ⚠️ **GitHub adapter** — `Octokit` 14.0.0 (REST). REST is sufficient: stable repo/PR numeric IDs are durable, the search API gives us the review-requested inbox, and we don't need anything GraphQL-only for v0.1.
- ⚠️ **ADO adapter** — raw `HttpClient` + `System.Text.Json`, **not** `Microsoft.TeamFoundationServer.Client`. The SDK is heavy and drags in older .NET dependencies; ADO REST is straightforward and lets me model the requests in terms we actually need.
- ⚠️ **ADO auth** — `Azure.Identity.AzureCliCredential`, resource id `499b84ac-1321-427f-aa17-267ca6975798`. Same pattern you used on 2026-05-13 for the ADO PR PATCH workaround.
- ⚠️ **Logging** — `Microsoft.Extensions.Logging` abstraction, Serilog for file sink at `%APPDATA%\PrInbox\logs\pr-inbox-.log` (daily rolling). Console output is via Spectre, not the logging pipeline — separation keeps logs clean and console pretty.
- ⚠️ **Tests** — xUnit 2.9.2 + FluentAssertions 7.0.0 + in-memory SQLite. Global usings for `Xunit` + `FluentAssertions` so test files stay tidy.

### Storage layout

- ⚠️ **DB location** — `%APPDATA%\PrInbox\pr-inbox.db` (per-user, not per-repo, not per-machine). On Jean-Marc's machine: `C:\Users\jmprieur\AppData\Roaming\PrInbox\pr-inbox.db`.
- ⚠️ **Config location** — `%APPDATA%\PrInbox\config.json`.
- ⚠️ **Review runs** — `%APPDATA%\PrInbox\reviews\<pr_safe_name>\<UTC-ts>_<head_sha[:12]>\`.
- ⚠️ **Logs** — `%APPDATA%\PrInbox\logs\pr-inbox-YYYY-MM-DD.log`.
- ⚠️ **Review-run dir naming** — `20260513T215935Z_abc123def456` (compact ISO-8601 with no colons; Windows-filesystem-safe).

### Schema

- ⚠️ **Timestamps** — ISO-8601 UTC strings stored as TEXT, e.g. `2026-05-13T22:01:13Z`. SQLite-native, easy to grep, sortable lexicographically.
- ⚠️ **Foreign keys** — `pragma foreign_keys = ON` per connection.
- ⚠️ **`ordered_commit_shas`** — JSON array stored as TEXT in `pr_snapshots`. We never query into it; we diff lists between snapshots. JSON is fine.
- ⚠️ **Snapshot dedup** — insert a new `pr_snapshots` row only if any tracked field changed since the previous snapshot. Otherwise just bump `pull_requests.last_synced_at`. Prevents log spam when syncing unchanged PRs frequently.
- ⚠️ **`identity_used` field** — which of your two identities (`jmprieur_public` or `jmprieur_microsoft`) saw the PR during sync. Lets us route the eventual `post` action to the right identity in v0.2.

### Sync behaviour

- ⚠️ **Per-source parallelism** — `Task.WhenAll` across sources during `sync`, with Spectre progress bars per source. Failures isolated per source; one source erroring never blocks another.
- ⚠️ **Transaction granularity** — one SQLite transaction per (source, identity) sync run. Either the source's snapshots all land or none do.
- ⚠️ **Soft-delete** — never hard-delete PR rows during sync. Set `status='inaccessible'` if the source returns 404; set `tracking_reason='previously_assigned'` if assignment goes away.
- ⚠️ **Rate limit handling** — simple exponential backoff on 429/503. GitHub gives 5000/hr authenticated; ADO is generous. We won't realistically hit these.

### List

- ⚠️ **Default sort** — by churn-since-last-brief descending (most-changed-since-I-looked first).
- ⚠️ **Default filter** — hide `closed` / `merged` / `archived` unless `--all` passed.
- ⚠️ **Source-staleness banner** — `list` shows a footer line summarizing the most recent `sync_runs` per source. If any source's last sync was `failed` or `rate_limited`, the banner is yellow and names the source + error.

### Review verb

- ⚠️ **Brief generation** — plain C# string templating (no Razor, no Scriban). Brief structure is stable; an engine would be overkill.
- ⚠️ **Stale-HEAD check** — the brief includes a `Verify HEAD is still <sha> before posting` clause. v0.1 trusts user discipline; v0.2 may add an automated pre-post check.

### Bot detection

- ⚠️ **Default bot list** — `copilot-pull-request-reviewer[bot]`, `Copilot`, `github-actions[bot]`. Plus GitHub's `author.type == "Bot"` flag as primary signal. Config can extend via `bots.extraLogins`.

---

## Needs your input (queued for morning)

Listed in priority order. Defaults applied so the build keeps moving, but
these are real decisions you may want to override.

### ❓ 1. GHE hostname — what is it?

Your `jmprieur_microsoft` identity lives on Microsoft's GHE. I don't know the
hostname. Probably `github.com` is wrong (.com is your public identity).
Examples I'd guess: `github.azure-azure.com`, `github.windows.com`,
`github.microsoft.com`, or similar. Without it, the GHE adapter is blocked
on smoke testing.

**Default applied:** `pr-inbox config add-source ghe.<host>` requires the host
as an argument. I'll leave the GHE smoke test for you to run after telling me
the host.

**Impact if wrong:** none until the GHE source is actually exercised; the
adapter code is host-agnostic.

### ❓ 2. ADO project list beyond `mseng/Context`

You said `mseng/Context/_git/Private` — that's an org/project/repo. I need just
the (org, project) pair: `mseng/Context`. From your current.md I can see you
also work in `mseng/Glasswing` repos; should I seed that project too? Other
ADO projects you actively review in?

**Default applied:** only `mseng/Context` is seeded. Add via
`pr-inbox config add-ado-project <org> <project>`.

**Impact:** you only see PRs from projects you've configured. Adding more later
is one command per project.

### ❓ 3. Which GitHub identity per host?

You have multiple identities (`jmprieur`, `jmprieur_microsoft`, `emu`, `proxima`
per `bridge.md`). On a single host, `gh auth login --hostname <host>` is per-host:
one active identity at a time. v0.1 assumes 1:1 (one identity per host) which
matches `gh`'s model.

**Discovered during Phase 4 smoke test:** Your `gh` on `github.com` is currently
logged in as **`jmprieur_microsoft`**, not `jmprieur`. The token providers
return that identity from `gh api user`. Two implications:

1. **Initial sync against github.com will pull PRs assigned to `jmprieur_microsoft`.** This is probably what you want for review work — your Microsoft identity is the one your team @-mentions.
2. **If you also have PRs assigned to `jmprieur` (your public/community identity) you want to track**, you'd need either `gh auth switch` (clunky) or multi-identity-per-host support (deferred to v0.1.5).

**Default applied:** identity = whatever `gh auth status --hostname <host>` says is the active user.

**Question for morning:** is it fine that github.com sync only sees `jmprieur_microsoft` assignments? Or do you also need `jmprieur`-as-public-identity coverage in v0.1?

### ❓ 4. Does ADO Copilot have a known service identity?

For bot detection on ADO threads, I don't know the display name / unique
identifier that Copilot uses when posting on ADO PRs. From `current.md` it
sounds like Copilot is active on ADO PRs but I haven't seen the exact author
field. Will discover on first sync; flagging so you tell me if you already
know it.

**Default applied:** ADO bot detection looks at known identity strings:
`Microsoft.Copilot`, `Copilot`. Will expand via config on first real PR.

**Impact:** ADO Copilot comments may be classified as human comments in the
first sync. Easy fix once we see the real identity.

### ❓ 5. Should `dotnet test` target the slnx or each project?

Minor — when I run `dotnet test PrInbox.slnx`, .NET 10 picks up the test
project correctly. Confirmed working. Just flagging in case you want a
top-level `Makefile`/`build.ps1` convenience that I haven't written yet.

**Default applied:** README documents `dotnet test PrInbox.slnx`. No
convenience script.

### ❓ 6. Should `pr-inbox config doctor` actually call platform APIs to verify?

The minimum is "check `gh auth status` and `az account show` succeed." The
maximum is "make a real `whoami` call per platform to verify the token works
and report the identity it resolves to." Default is the maximum because
identity mismatches are silently common.

**Default applied:** doctor calls platform whoami endpoints, reports the
resolved identity per source. Never prints token values.

### ❓ 7. Where should briefs live for shareability?

`%APPDATA%\PrInbox\reviews\…` is per-user, not synced. If you ever wanted
briefs to land in OneDrive (so a different machine / future you could read
them), the path would need to be configurable. v0.1 hardcodes `%APPDATA%`.

**Default applied:** hardcoded. Will add `config set reviews-path <path>` if
you signal the need.

### ❓ 9. `stable_identity` for GitHub uses `repo_id=0` for now

**Discovered Phase 5 smoke.** Octokit's search-issues API populates `Issue.Id`
(the unique PR id) but **not** `Issue.Repository` — so the search-result mapping
can't construct the real numeric repo id. Stable identity falls back to
`gh.com:0#<pr_id>`. The PR id alone is globally unique on github.com so
collisions are safe, but the `0` looks weird.

**Default applied:** `repo_id=0` from search; real repo id is populated when we
go through `GetPullRequestDetailAsync` (which calls `/repos/.../pulls/...`).

**Fix path:** make `GetReviewInboxAsync` do a cheap follow-up `/repos/...` call
per PR to enrich the stable id. Or compute stable id from the PR URL
(`/repos/owner/repo/pull/N` doesn't give repo id either, only owner+repo). The
correct path is a GraphQL query that bundles search + repo node ids.

**Impact:** the `pull_requests.stable_identity` value carries `0` for repo id
on PRs we've only seen via search. If a repo renames AND the PR id alone is
ambiguous somehow (it isn't), the join would break. Real-world impact: none.
Flagging for cleanup in v0.1.5.

### ❓ 10. `reviewer_state` is best-effort

The Reviews API returns the *list of reviews* on a PR, but we'd need to know
the active gh user to filter for "your review state." v0.1 just maps
"reviews exist → Commented" else "Requested." Good enough for display in
`list`, but if you have a `dismissed` or `changes_requested` review on a PR,
v0.1 will report `Commented`.

**Fix path:** cache the authenticated user identity from `gh api user` once
at startup, then filter reviews by `review.User.Login == activeUser`. Lands
in v0.1.5.

---

## Build journal

*Phase-by-phase notes on what I built, what I learned, and what I'd change.*

### Phase 1 — Bootstrap ✅

**Goal:** runnable empty CLI with passing test runner, all infrastructure for later phases.

**Done:**
- Repo at `D:\1es\pr-inbox`, branch renamed `master` → `main`.
- Local git author = Jean-Marc; Co-authored-by Copilot trailer pattern.
- `.gitignore`, `.gitattributes`, `LICENSE` (MIT), `global.json` pinning .NET 10.0.202.
- `Directory.Build.props` (nullable, implicit usings, warnings-as-errors).
- `Directory.Packages.props` (CPM, all package versions centralized).
- `NuGet.config` (nuget.org only — resolved the NU1507 CPM-with-multiple-sources block).
- Solution `PrInbox.slnx` with 4 projects.
- First migration `001_initial.sql` with all 6 tables + indexes.
- Stub `Program.cs` with FigletText banner.
- README.md, ARCHITECTURE.md, AMBIGUITIES.md, BootstrapSmokeTests passing.

**Lessons:**
- .NET 10 produces `.slnx` (XML solution) by default.
- CPM + multiple NuGet sources requires `packageSourceMapping` explicitly (NU1507).

### Phase 2 — Source contract ✅

**Goal:** domain model + read-only contract + fake source + characterization tests.

**Done:**
- `PrIdentity` readonly record struct with display + stable formatters for all 3 sources.
- Enum set: `PullRequestStatus`, `TrackingReason`, `ReviewerState`, `ThreadKind`, `BotKind`, `ReviewRunStatus`, `SyncRunStatus`.
- `SourceCapabilities` record with 6 flags.
- DTOs: `RemotePullRequest`, `RemotePullRequestDetail`, `RemoteThread`, `RemoteCommit`, `CompareResult`.
- `IPrReadSource` interface (read-only by construction).
- `FakePrReadSource` + builder; default capabilities mirror platform truths (ADO has no global inbox).
- 18 tests added (19 total with the bootstrap smoke).

### Phase 3 — Storage layer ✅

**Goal:** SQLite-backed registry with migration runner and all repositories.

**Done:**
- `PrInboxDb` + `MigrationRunner` (embedded SQL, version-tracked, backup-before-migrate, idempotent).
- `PullRequestRepository`, `PrSnapshotRepository` (with dedup), `ObservedThreadRepository` (preserves first_seen, advances last_seen, resolves), `ReviewRunRepository` (immutable), `SyncRunRepository`.
- 12 tests added (33 total).
- `InternalsVisibleTo PrInbox.Tests` added so embedded-migration internals are testable.

**Lesson:**
- Migrations must not declare `schema_version`; the runner owns it. First version of `001_initial.sql` did, and tests caught it on the second run.

### Phase 5 — GitHub adapter ✅

**Goal:** read-only Octokit adapter for github.com + GHE.

**Done:**
- `BotDetector` (pure function, 10 tests): GitHub `type=="Bot"` + cascade through known Copilot/dependabot/github-actions/`[bot]` suffix logins, + configurable extra logins.
- `GitHubReadSource` implementing all 5 IPrReadSource methods using Octokit REST. Single class handles .com (default `api.github.com`) and GHE (`https://<host>/api/v3/`).
- 5 parse tests for display-identity round-tripping.
- `--smoke-github` CLI flag (live verification).
- **Live verified: 60 PRs pulled from `jmprieur_microsoft`'s github.com inbox.** Bot classification works (`dependabot[bot]` → Dependabot, `copilot-pull-request-reviewer[bot]` → CopilotReview, `[bot]` fallback for `microsoft-github-policy-service[bot]`).

**Lessons:**
- Octokit ships its own `CompareResult` type colliding with our domain type. Resolved with a `using` alias at the call site.
- Octokit's `PullRequestReview.SubmittedAt` is non-nullable `DateTimeOffset` in v14.0.0 (older versions were nullable); changed signature.
- `Issue.Repository` is **null on search API responses** — Octokit only populates it on direct `/repos/.../issues/...` calls. Stable identity falls back to `repo_id=0` for now (see AMBIGUITIES §9 below).

### Phase 6 — Sync orchestrator + Phase 7 List + Phase 8 Review + CLI wiring ✅

**Goal:** end-to-end vertical slice — sync writes to SQLite, list reads it, review generates brief.

**Done:**
- `SyncOrchestrator` (PrInbox.Sources): one run per (source, identity), records `sync_runs` row from start to finish (always finalized in `finally`), reconciles missing-from-inbox PRs to `previously_assigned`, per-PR errors don't tank the run.
- `SourceFactory` translates `PrInboxConfig` → runtime `IPrReadSource` instances. Loudly NotImplementedException on Azure DevOps (the gap is named so it can't be silently dropped).
- `SyncCommand`: progress via `Spectre.Console.Status` spinner; ADO sources are filtered out with a yellow warning rather than failing.
- `ListCommand`: Spectre table with PR / Title / Age / Churn / Bot / Open / Reason. Source-freshness footer summarizing `sync_runs`. Computes churn as `force-pushed` / `+N commits` / `clean` / `new` from the latest snapshot's commit list.
- `ReviewCommand`: creates `%APPDATA%\PrInbox\reviews\<safe_pr>\<utc-ts>_<sha[:12]>\` directory, writes `brief.md` + `metadata.json`, inserts `review_runs` row, updates `last_briefed_head_sha`. Brief is rich: PR meta, state SHAs, what-changed-since-last (with explicit force-push handling), open threads, recent bot activity, standard dual-model-review invocation block, staleness clause.
- `ConfigCommands`: `init`, `doctor`, `add-source`, `add-ado-project` subcommands.

**End-to-end live verification on Jean-Marc's machine:**
1. `pr-inbox config init` → seeded `C:\Users\jmprieur\AppData\Roaming\PrInbox\config.json` with github.com + Copilot bot seed.
2. `pr-inbox config doctor` → verified gh auth, identity `jmprieur_microsoft`.
3. `pr-inbox sync` → **60 PRs pulled in ~60 seconds**, snapshots stored, threads observed, bot detection working.
4. `pr-inbox list` → rendered triage table for all 60 PRs with churn/bot/open columns. (PowerShell console mangled the box-drawing chars on display but the data is intact and a real terminal renders it cleanly.)
5. `pr-inbox review gh.com:1ES-microsoft/ai-plugins#90` → produced brief.md with PR meta, state SHAs, open threads (Copilot + policy bot), recent bot activity (CopilotReview + Other), dual-model-review invocation block, staleness clause pointing at HEAD SHA. Brief lives at `%APPDATA%\PrInbox\reviews\gh.com_1ES-microsoft_ai-plugins_90\20260514T053211Z_2e49b0c422d5\brief.md`.

**All 53 unit tests still green.**

**Final state:**
- 6 commits on `main` branch (one per phase milestone).
- Read-only against platforms by construction (`IPrReadSource` is the only contract).
- No tokens stored anywhere; delegated to `gh` + `az`.
- Append-only snapshot/thread/review-run history in SQLite.
- Immutable review-run directories that can be replayed.
- All 5 v0.1 verbs working end-to-end against real GitHub data.
