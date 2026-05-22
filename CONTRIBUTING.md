# Contributing to pr-inbox

Thanks for taking a look — this repo started as a personal harness and
grew into something a small group of us use daily. PRs that make it
better for everyone in that group are very welcome.

This guide is intentionally short. If you can clone, build, run the
tests, and find a good first thing to work on, you have everything
you need.

---

## Quick orientation

- **What it is:** a personal multi-source PR review inbox (GitHub.com,
  GitHub Enterprise, Azure DevOps) with a Blazor Server companion UI
  and a Copilot CLI handoff for `dual-model-review`.
- **What it isn't:** a code review engine. The review itself is done
  by `dual-model-review` in a separate Copilot session; this repo
  *aggregates*, *tracks*, and *briefs*.
- **Why it exists:** see [README.md → Why this exists](README.md#why-this-exists).
- **Surface-by-surface tour:** [USER_GUIDE.md](USER_GUIDE.md).

For deeper architectural background, the design notes in
[`plan.md`](plan.md) (and the session checkpoints) are kept
chronological — most "why is it like this?" questions are
answerable from there.

---

## Build, run, test

Prereqs: **.NET 10 SDK**, **git**, and (for Review handoff)
`gh` CLI + Copilot CLI. The full list with version pins is in
[README.md → Install](README.md#install).

```powershell
dotnet restore PrInbox.slnx
dotnet build   PrInbox.slnx
dotnet test    PrInbox.slnx
```

To run the Web UI locally:

```powershell
.\Start.bat               # opens splash, then http://localhost:7341
# or:
dotnet run --project src\PrInbox.Web
```

CLI:

```powershell
dotnet run --project src\PrInbox.Cli -- sync
dotnet run --project src\PrInbox.Cli -- list
```

If the tests pass and the Web UI loads on `http://localhost:7341`,
you're set up.

---

## Conventions

The codebase is small enough that consistency matters more than
ceremony.

- **Language / style**
  - C# 13, nullable enabled, implicit usings, file-scoped namespaces.
  - Treat warnings as errors. If the build is yellow on your branch,
    it's red on main.
  - Comments explain *why*, not *what*. Prefer expressive names over
    inline narration.
  - Async + `CancellationToken` everywhere — even on methods that
    don't yet await anything, if they cross a boundary.
- **Tests**
  - xUnit + FluentAssertions; in-memory SQLite for storage round-trips.
  - Add a test with every behavior change. The test count in the
    README badge should never go down.
  - Storage migrations get a dedicated round-trip test (see
    `tests/PrInbox.Tests/Storage/RepositoryRoundTripTests.cs` for the
    pattern).
- **Logging**
  - `Microsoft.Extensions.Logging` only. The Serilog file sink lives
    under `%APPDATA%\PrInbox\logs\`. Don't `Console.WriteLine`.
- **Read-only by construction**
  - `PrInbox.Cli` never references `PrInbox.Publishers`. This is
    enforced at the csproj level and is a load-bearing invariant —
    keep it that way. (Same reason the CLI is the safe surface for
    cron-style usage.)
- **UI**
  - Blazor Server. Prefer adding small components in
    `Components/Pages` over inflating `Inbox.razor`, *unless* the
    new code is so tightly coupled to inbox state that splitting it
    would just create a prop-drilling problem.
  - CSS goes in `wwwroot/css/site.css`. No new CSS frameworks.

---

## Branch and PR conventions

- Branch off `main`. Naming: `feat/<short-slug>`, `fix/<short-slug>`,
  `docs/<short-slug>`, `refactor/<short-slug>`.
- Commit messages use [Conventional Commits](https://www.conventionalcommits.org/)
  prefix where natural: `feat(tags):`, `fix(inbox):`, `docs(readme):`,
  `refactor(storage):`. The bodies in `git log` are a good reference.
- Keep PRs focused. One feature or one fix per PR. If you discover an
  unrelated bug, open a separate issue and keep the original PR clean.
- The PR description should include:
  - One sentence: what changes for the user.
  - One paragraph: what changes in the code (which files / why).
  - Test results (`dotnet test` output line is enough).
  - Screenshots or a short clip if it's a UI change.

---

## Filing issues

A good issue is one we can act on without coming back to ask. Please
include:

- **What you did** (clicks, CLI invocation, config edits)
- **What you expected**
- **What happened instead** — paste the relevant lines from
  `%APPDATA%\PrInbox\logs\` if it's a runtime problem
- **Environment**: `dotnet --version`, Windows vs not, fresh clone
  or upgrade

If you've found something that smells like a security issue (auth
bypass, accidental token logging, unsafe deserialization in a
publisher path) — please **don't open a public issue**. Send a
direct message to @jmprieur instead.

---

## Good first issues

Look for the [`good first issue`](https://github.com/jmprieur/pr-inbox/issues?q=is%3Aissue+is%3Aopen+label%3A%22good+first+issue%22)
label. These are intentionally scoped to be self-contained, well-
specified, and small enough to land in a single PR.

If you're between projects and want to pick one up, just claim it in
the issue thread first so we don't double up.

---

## Trust boundaries (read before touching these files)

A handful of files sit at trust boundaries — bugs there have
non-local consequences. Changes are welcome, but please open an
issue first so the design discussion is recorded:

- `src/PrInbox.Core/Storage/PrInboxDb.cs` and any migration in
  `Migrations/` — schema is append-only and the migration ordering
  is load-bearing.
- `src/PrInbox.Core/Storage/TagRepository.cs` and other repository
  classes — they own SQLite cancellation/transaction discipline.
- `src/PrInbox.Web/Services/ReviewLauncher.cs` — shells out to
  `gh` / `copilot`; argument shape and quoting are security-relevant.
- Identity wiring in `src/PrInbox.Web/Services/InboxSyncHostedService.cs`
  and `IConfigService` — the multi-identity contract took several
  rounds to lock down.
- Anything under `src/PrInbox.Publishers/` — `Cli` cannot reference
  this assembly, on purpose. Don't add a reference.

---

## License

By contributing you agree your contributions are licensed under the
[MIT License](LICENSE), same as the rest of the repo.
