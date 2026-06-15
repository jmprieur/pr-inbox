# pr-inbox — "My PRs" (authored view) design

*Design note for adding an authored-PR view alongside the reviewer inbox.
Read alongside `ARCHITECTURE.md` (rationale) and `AMBIGUITIES.md` (the three
open questions this note raises live there as ❓ 11–13).*

*Status: proposed, not yet implemented. — Bridge, 2026-06-14*

---

## The question

Jean-Marc asked whether "PRs I authored" can be surfaced by **filtering the
existing inbox on author = me**, or whether it needs **its own tab**.

## The finding that decides it

The inbox is built from exactly two reviewer-side search queries, unioned in
`GitHubReadSource.InboxQueries`:

```
is:pr is:open review-requested:@me
is:pr is:open reviewed-by:@me
```

Everything those return is stored with `TrackingReason.Assigned`, and
`pull_requests.author_login` holds the *PR's* author — which, in a reviewer
inbox, is **other people**. The existing author control in the Web UI
(`_excludedAuthors`) is an *exclude* filter over that reviewer population.

**Therefore "just filter by author = me" cannot work as-is.** PRs you authored
(`author:@me`) are never fetched into SQLite. Filtering can't surface a
population sync never pulled. The real work is a **new fetch**; where it lands
in the UI is a second, smaller decision.

## Decision

Authored PRs are a **separate population**, surfaced in a **separate `/my-prs`
view**, distinguished by an **orthogonal `my_role` dimension** — not a filter on
the existing inbox. This is faithful to the architecture's existing principle
that lifecycle concerns are orthogonal (status ⟂ tracking_reason); role joins
them as a third orthogonal axis.

Authored PRs answer a different question, with different columns:

| Review inbox asks | "My PRs" asks |
|---|---|
| Do I need to review this? Unread threads? My reviewer state? | Is CI green? Who's reviewed/approved? Changes-requested I must address? Mergeable? |

Different columns, different empty-state, different default sort → a separate
view, consistent with the app already splitting concerns across pages
(`Inbox.razor`, `Threads.razor`).

---

## 1. Data model — migration `014_my_role.sql`

```sql
-- pull_requests gains a role dimension, orthogonal to tracking_reason/status.
ALTER TABLE pull_requests ADD COLUMN my_role TEXT NOT NULL DEFAULT 'reviewer';
--   values: 'reviewer' | 'author' | 'both'
CREATE INDEX idx_pull_requests_my_role ON pull_requests(my_role);
```

- Backfill is implicit: every existing row = `'reviewer'` (correct — they all
  came from reviewer queries).
- New enum `MyRole { Reviewer, Author, Both }` in `Enums.cs`, plus
  parse/serialize in `PullRequestRepository` mirroring
  `ParseTrackingReason` / `TrackingReasonToDb`.

### The one schema tension (see ❓ 11 in AMBIGUITIES.md)

`tracking_reason` is `NOT NULL` and models the *reviewer* lifecycle
(`assigned → previously_assigned → archived`). An **author-only** PR has no
reviewer lifecycle.

**Recommended:** add a sentinel `tracking_reason = 'not_reviewer'` for
author-only rows (cheap; no SQLite table rebuild). This does **not**
reintroduce a `TrackingReason.Authored` *role* value — it is a lifecycle state
meaning "reviewer lifecycle N/A," keeping role and lifecycle orthogonal.

**Alternative:** make `tracking_reason` nullable — cleaner conceptually but
requires a SQLite table rebuild (SQLite can't drop `NOT NULL` in place).

---

## 2. Source layer

- `IPrReadSource`: add
  `IAsyncEnumerable<RemotePullRequest> ListAuthoredFastAsync(CancellationToken ct)`.
- `SourceCapabilities`: add `SupportsAuthoredInbox` — GitHub/GHE = `true`;
  ADO = `false` initially (per-project `creatorId` filter; same capability-gated
  pattern as `SupportsGlobalReviewerInbox`).
- `GitHubReadSource`: a third query, kept separate from `InboxQueries`:

  ```
  is:pr is:open author:@me
  ```

  Rows yielded here carry role `Author` (carried on `RemotePullRequest`, or
  tagged at the orchestrator boundary).

---

## 3. Sync orchestration (the subtle part)

- New authored pass alongside `ListAssignedFastAsync`. A PR returned by **both**
  the reviewer and authored queries → `my_role = 'both'`.
- **Role-scope the disappear sweep.** Today `SyncOrchestrator` flips any vanished
  `Assigned` row to `PreviouslyAssigned`. That sweep must run **only** for rows
  whose role includes *reviewer*. An authored PR dropping out of `author:@me`
  (you merged/closed it) must **not** be marked previously-assigned-reviewer —
  it simply leaves the authored set (or moves to a "recently merged" tail; see
  ❓ 12).

---

## 4. Web — `MyPrs.razor` (`@page "/my-prs"`)

- New `PullRequestRepository.ListAuthoredAsync()`:
  `WHERE my_role IN ('author','both') AND status = 'open'`.
- Reuse the table / source-chip infrastructure; swap the column set for the
  authored question:

  | Inbox columns | My PRs columns |
  |---|---|
  | Author, reviewer state, unread threads | **CI status**, **review decision** (approved / changes-requested / none), **# unresolved threads I must address**, **mergeable** |

- Default sort: changes-requested + stale on top.
- Add a "My PRs" nav entry beside Inbox / Threads. The existing author
  exclude-filter on `/` is untouched.

---

## 5. Testing

- `GitHubReadSource` fake: authored query returns rows; dedupe against the
  reviewer set yields `both`.
- Orchestrator: reviewer-disappear sweep ignores author-only rows (regression
  guard for the role-scoping above).
- Migration `014` round-trip + backfill = `'reviewer'`.

---

## 6. Phasing

1. Model + migration `014` + repo methods (+ tests)
2. GitHub source authored query + `SupportsAuthoredInbox` + role tagging
3. Orchestrator role-scoped sync / sweep
4. `MyPrs.razor` view
5. ADO / GHE authored support (later; capability-gated)

---

## Open questions

Tracked as ❓ 11–13 in `AMBIGUITIES.md`:

1. **Author-only `tracking_reason`** — `'not_reviewer'` sentinel (recommended,
   cheap) vs. nullable column (cleaner, needs table rebuild).
2. **Closed/merged authored PRs** — drop on disappear, or keep a short
   "recently merged" tail (the append-only snapshot model supports it).
3. **Enrichment** — do authored PRs need full thread enrichment (to count
   "comments I must address"), or is list-tier enough for v1?
