# pr-inbox

> A personal command-line PR review inbox across **GitHub.com**, **GitHub Enterprise**, and **Azure DevOps**.
> Aggregates assigned PRs, tracks per-PR review state across sessions, and bootstraps a Copilot review session with full context.

[![Status: alpha](https://img.shields.io/badge/status-alpha-orange)](#status)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)

`pr-inbox` is the harness for review-at-scale. It does not review code itself —
it tells you which PRs need attention, what changed since the last time you
looked, and hands a fully-bootstrapped brief to the Copilot session that
will run the actual `dual-model-review`.

---

## Why this exists

Reviewing many PRs at scale across three platforms with two identities is
manual. The pain is not the review skill itself — that's mature
(`dual-model-review` with Opus 4.7 + GPT-5.5, asymmetry pattern stable at N=6).
The pain is:

| Pain | What `pr-inbox` does about it |
|---|---|
| No single inbox across GitHub.com / GHE / ADO | `sync` pulls all sources; `list` shows one unified view |
| Per-PR state lost between sessions (last reviewed SHA, posted comments, resolved threads) | Local SQLite registry; immutable snapshots; never hard-deletes |
| Re-entry cost — "let me check that PR again" rebuilds context from zero | `review` produces an immutable brief.md with diff-since-last + open threads + recent bot comments |
| Curation tracking (the 95%+ inline filter) is in your head only | Each `review` creates a recorded review run; curation lands in v0.2 |
| Convergence/asymmetry telemetry hand-counted | Append-only schema captures the data; queries land in v0.3 |

---

## Status

**Alpha — v0.1 in progress.** Scope:

| Verb | Purpose | Status |
|---|---|---|
| `pr-inbox config` | Manage sources, identities, ADO projects | Pending |
| `pr-inbox sync` | Pull PRs assigned to me across enabled sources; snapshot platform state | Pending |
| `pr-inbox list` | Triage table: age, churn, bot comments, open threads, tracking reason | Pending |
| `pr-inbox review <id>` | Generate immutable brief.md + recommended `copilot` command | Pending |

**Out of scope for v0.1** (deferred to v0.2/v0.3): `curate`, `post`, `followup`,
thread resolution, telemetry queries.

**v0.1 is read-only against platforms by construction.** The source adapters
implement `IPrReadSource` only — a future `IPrReviewPublisher` is a separate
type the v0.1 binary cannot accidentally call.

---

## Install

Prerequisites:

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [`gh`](https://cli.github.com/) — GitHub CLI, authenticated for github.com and any GHE host you use
- [`az`](https://learn.microsoft.com/cli/azure/install-azure-cli) — Azure CLI, signed in (`az login`)

From source (until published to NuGet):

```powershell
git clone <repo-or-path>
cd pr-inbox
dotnet pack -c Release src/PrInbox.Cli/PrInbox.Cli.csproj
dotnet tool install --global --add-source src/PrInbox.Cli/nupkg JmPrieur.PrInbox
```

Verify:

```powershell
pr-inbox --help
pr-inbox config doctor   # checks gh + az auth, ADO project access
```

---

## Quick start

```powershell
# 1. Initialize config (one time)
pr-inbox config init

# 2. Add the sources you want to track
pr-inbox config add-source github.com
pr-inbox config add-source ghe.<your-host>          # optional
pr-inbox config add-ado-project mseng Context       # one per ADO project

# 3. Verify auth is working
pr-inbox config doctor

# 4. Pull your inbox
pr-inbox sync

# 5. See what needs attention
pr-inbox list

# 6. Start a review session on a specific PR
pr-inbox review gh.com:agency-microsoft/playground#4248
# Prints: brief path + recommended `copilot` invocation
```

---

## How it works

```
┌────────────────────┐
│ Source adapters    │
│  • GitHub (.com)   │
│  • GitHub (GHE)    │  ──┐
│  • Azure DevOps    │    │  IPrReadSource
└────────────────────┘    │
                          ▼
┌─────────────────────────────────────────┐
│ SQLite registry                         │
│ %APPDATA%\PrInbox\pr-inbox.db           │
│                                         │
│ • pull_requests (current row)           │
│ • pr_snapshots (append-only)            │
│ • observed_threads (append-only)        │
│ • review_runs (immutable)               │
│ • posted_reviews (v0.2+)                │
│ • sync_runs (per-attempt status)        │
└─────────────────────────────────────────┘
                          │
                          ▼
┌─────────────────────────────────────────┐
│ Verbs                                   │
│  sync ─── refresh registry              │
│  list ─── triage table                  │
│  review ─ immutable run dir + brief.md  │
│  config ─ identities / projects / doctor│
└─────────────────────────────────────────┘
```

### Credentials — delegate, never store

`pr-inbox` does **not** store tokens, ever. It delegates to the credential
authorities you already use:

| Source | Token path |
|---|---|
| GitHub.com | `gh auth token --hostname github.com` |
| GitHub Enterprise | `gh auth token --hostname <ghe-host>` |
| Azure DevOps | `Azure.Identity.AzureCliCredential` (uses `az` under the hood; resource `499b84ac-1321-427f-aa17-267ca6975798`) |

Why: no PATs to manage, no secret storage to harden, no leakage risk. Tokens
are minted on demand and never written to disk by this tool.

### Per-PR identity

| Platform | Display identity | Stable identity (durable key) |
|---|---|---|
| GitHub.com | `gh.com:owner/repo#N` | `gh.com:<repo_id>#<pr_id>` |
| GitHub Enterprise | `ghe.<host>:owner/repo#N` | `ghe.<host>:<repo_id>#<pr_id>` |
| Azure DevOps | `ado:<org>/<project>/<repo>#N` | `ado:<org>/<projectGuid>/<repoGuid>#N` |

Display id is what humans/commands use. Stable id is the join key the registry
trusts when repos/projects rename.

### The `review` verb in detail

`pr-inbox review <id>` is the verb that earns the tool's existence. It:

1. Does a single-PR fast-path `sync` to refresh that one PR's snapshot.
2. Computes what's new since `last_briefed_head_sha` (commits, force-push, base
   change, new threads, new bot comments, newly-resolved threads).
3. Creates an **immutable** run directory:
   `%APPDATA%\PrInbox\reviews\<pr_dir>\<UTC-ts>_<head_sha[:12]>\`
4. Writes `brief.md` containing:
   - PR identity + URLs + author + title
   - Head/base SHAs, last-briefed / last-reviewed / last-posted SHAs
   - Diff summary since last brief (commits, force-push, base change)
   - Embedded unified diff up to 50KB; beyond that, file list + diff URL
   - My open threads with status
   - Recent bot comments (Copilot review, Copilot coding agent) since last brief
   - Standard dual-model-review invocation block (Opus 4.7 + GPT-5.5, asymmetry
     instructions, `do NOT post`, `diff_anchorable` flag, 95%+ inline filter)
   - Staleness clause ("verify PR HEAD is still `<sha>` before posting")
5. Writes `metadata.json` (machine-readable mirror).
6. Inserts `review_runs` row; updates `pull_requests.last_briefed_head_sha`.
7. Prints the brief path and the recommended `copilot` command for you to run.

Re-running `pr-inbox review <id>` **always** creates a new immutable run.
A `--launch` flag that invokes `copilot` directly is deferred to v0.2.

---

## Configuration

`%APPDATA%\PrInbox\config.json` — managed via `pr-inbox config` verbs.
Holds source definitions + ADO project list + bot login overrides.
**Never contains tokens.**

Example:

```json
{
  "schemaVersion": 1,
  "sources": [
    { "id": "gh.com", "kind": "github", "host": "github.com", "identity": "default" },
    { "id": "ghe.contoso", "kind": "github-enterprise", "host": "github.contoso.com", "identity": "default" }
  ],
  "ado": {
    "projects": [
      { "org": "mseng", "project": "Context" }
    ]
  },
  "bots": {
    "extraLogins": ["Copilot"]
  }
}
```

---

## Repository layout

```text
pr-inbox/
├── src/
│   ├── PrInbox.Cli/        # global tool entry point
│   ├── PrInbox.Core/       # domain, storage, credentials
│   └── PrInbox.Sources/    # GitHub + ADO adapters; fake source for tests
├── tests/
│   └── PrInbox.Tests/      # xUnit + FluentAssertions, in-memory SQLite
├── README.md               # this file
├── ARCHITECTURE.md         # design rationale + rubber-duck critique log
├── AMBIGUITIES.md          # open design decisions (read first in the morning)
├── LICENSE                 # MIT
├── global.json             # pins .NET 10.0.202
├── Directory.Build.props   # nullable, implicit usings, treat warnings as errors
├── Directory.Packages.props# central package management
├── NuGet.config            # nuget.org only
└── PrInbox.slnx            # .NET 10 XML solution
```

---

## Development

```powershell
dotnet restore PrInbox.slnx
dotnet build PrInbox.slnx
dotnet test PrInbox.slnx
```

Conventions:

- C# 13, nullable enabled, implicit usings, file-scoped namespaces
- Treat warnings as errors (CI build matches local build)
- xUnit + FluentAssertions; in-memory SQLite for storage tests
- Async + cancellation tokens everywhere
- Logging via `Microsoft.Extensions.Logging` (Serilog file sink under `%APPDATA%\PrInbox\logs\`)

---

## License

MIT. See [LICENSE](LICENSE).

---

## Acknowledgements

Co-created by **Jean-Marc Prieur** and **Bridge** (Claude / Copilot CLI),
2026-05-13 onward. Plan and rubber-duck critique in
[ARCHITECTURE.md](ARCHITECTURE.md); open design decisions in
[AMBIGUITIES.md](AMBIGUITIES.md).
