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

### ❓ 8. Smoke test against your actual auth

When I get to Phase 9, I'll run `pr-inbox config doctor` + `pr-inbox sync`
against your real `gh` and `az` auth. This will read from GitHub / ADO and
write the SQLite registry — but **no platform mutation** (read-only verbs
only). Flagging because it's the first time the tool touches your real
inbox.

**Default applied:** I will run the smoke. The DB / config / logs land in
`%APPDATA%\PrInbox\` and are .gitignored.

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

### Phase 4 — Credentials ✅

**Goal:** token providers that delegate to `gh` and `az`. No secret storage anywhere.

**Done:**
- `ITokenProvider` + `TokenAcquisitionException` with human-actionable remediation messages.
- `GhCliTokenProvider` shells out to `gh auth token --hostname <host>`; also implements `GetAuthenticatedIdentityAsync` via `gh api user`.
- `AzureCliTokenProvider` uses `Azure.Identity.AzureCliCredential` for ADO (resource id `499b84ac-1321-427f-aa17-267ca6975798`); identity via `az account show --query user.name`.
- `PrInboxConfig` JSON model (sources, ADO projects, bot overrides) with `LoadAsync`/`SaveAsync`. `PR_INBOX_CONFIG_PATH` env override for tests.
- `--smoke-tokens` hidden flag wired through `Program.cs` for live verification.
- 3 tests added for config round-trip (36 total).

**Live smoke verification on Jean-Marc's machine:**
- GitHub.com via `gh`: ✅ token length 40, identity **`jmprieur_microsoft`** (not `jmprieur`!)
- Azure DevOps via `Azure.Identity`: ✅ JWT length 2622, identity `jmprieur@microsoft.com`

**Lessons:**
- `az` is shipped as `az.cmd` on Windows. `Process.Start` with `UseShellExecute=false` can't launch `.cmd` files directly; wrap with `cmd.exe /c az ...`. (`Azure.Identity` already handles this internally, but our ancillary identity-discovery shell-out did not.)
- **`gh` on github.com is logged in as `jmprieur_microsoft`, not `jmprieur`.** This means our github.com queries will see PRs assigned to `jmprieur_microsoft`. If you also have `jmprieur` PRs to track, you'd need to either `gh auth switch` or set up multi-identity-per-host (deferred to v0.1.5). Flagged below in §3.

**Next:** Phase 5 — GitHub adapter (Octokit-based, .com + GHE).
