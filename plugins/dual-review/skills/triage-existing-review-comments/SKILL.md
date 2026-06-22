---
name: triage-existing-review-comments
description: >-
  Adjudicates the review comments already on a PR (bot reviewers like
  Copilot by default): verifies each against the code at HEAD, replies
  with the rationale, thumbs-up the correct ones, and resolves threads
  that are settled AND verified -- either the author addressed it and we
  confirmed the fix, or the author rejected it with evidence and we
  agree. Leaves genuinely open or contested threads alone. Dry-run by
  default; mutates only on an explicit execute signal.
metadata:
  author: pr-inbox maintainers
  version: "0.1.0"
  category: review
  severity: medium
  triggers:
    - "Operator: 'reply to / resolve the Copilot comments on this PR'"
    - "A PR has open bot-reviewer threads that need adjudication"
    - "Reviewer wants existing review comments triaged, not new findings posted"
---

# Triage Existing Review Comments

> **Purpose:** Help the PR author reach a clean review surface. For each
> review comment already on the PR, decide whether it is correct,
> already-addressed, or wrong; reply with that judgment and the
> evidence; and **resolve the thread when it is settled and we verified
> it** -- so the author is not left to manually close threads that are
> demonstrably done. This codifies a loop the operator was running by
> hand on every review ("reply to the Copilot comments; thumbs-up the
> right ones; resolve the ones the author fixed or rightly rejected").

## Context

`dual-model-review` + `post-dual-review-as-pr-comments` move in the
**outbound** direction: produce *our* findings, post *our* findings.
This skill is the **inbound** direction: take the comments that are
*already on the PR* (typically from automated reviewers such as
Copilot, but optionally any author) and adjudicate them.

The work is judgment plus a small set of mutating GitHub actions
(reply, reaction, resolve) that are easy to get subtly wrong --
resolving a thread that is actually still open papers over a real bug;
replying without verifying turns the reviewer into a rubber stamp;
mutating a third party's PR without a gate is socially costly. Those
sharp edges are why this is its own skill rather than ad-hoc
orchestrator prose.

It sits **after** the bot reviewers have commented and **before** (or
alongside) human review. It is a sibling of
`post-dual-review-as-pr-comments` and shares its delivery machinery:
`gh` auth, HEAD-staleness pinning, UTF-8-safe payloads, thread
resolution via GraphQL, and idempotency markers.

## When to use

- A PR has unresolved review comments from a bot reviewer (Copilot,
  a linter bot, an analyzer) and the reviewer wants them triaged.
- The author has pushed fixes and the bot threads are now stale but
  still showing "unresolved", cluttering the author's view.
- You ran `dual-model-review` and want to reconcile its verdict with
  the comments already on the PR (agree / second / refute) rather than
  posting a parallel, duplicate set of findings.

## When NOT to use

- You want to post *new* findings from a review you just ran -- that is
  `post-dual-review-as-pr-comments`.
- The threads are from human reviewers and you have not been asked to
  touch human conversations. Default `comment_filter` is `bots_only`;
  do not resolve a human's thread without explicit opt-in.
- You do not have permission to comment on / resolve threads on the
  target repo (resolution needs triage/write).
- The PR is on Azure DevOps. ADO PR threads use a different API; that
  is a sibling skill.

## Inputs

| Input | Description |
|---|---|
| `pr_ref` | PR identity. One of: `owner/repo#N`, full URL, or `(owner, repo, number)` triple. |
| `auth` | Caller-provided GitHub auth (typically the `gh` CLI session). The skill uses `gh api` / `gh api graphql`; it never embeds a PAT. |
| `comment_filter` | Which threads to adjudicate. `bots_only` (default) restricts to automated reviewers (login matches `copilot`, `*[bot]`, or a configured allowlist). `all` includes humans. `authors: [...]` is an explicit login allowlist. |
| `mode` | `dry_run` (**default**) prints the adjudication table and the draft actions and mutates **nothing**. `execute` performs the replies/reactions/resolves. Treat a caller "go / execute / I trust you" as `execute`; absent that, stay in `dry_run`. |
| `resolve_policy` | Override of which settled states resolve. Default `verified_settled` (see policy table). Other values: `rejected_and_agree` (only the author-rejected case), `incorrect_only`, `never` (reply-only). The verification gate (§Step 2) is **not** overridable -- nothing resolves without our own evidence. |
| `verdict` | Optional `dual-model-review` verdict to cross-reference. When a finding in the verdict overlaps an existing comment, reuse our already-verified conclusion as the adjudication evidence instead of re-deriving it. |

## Adjudication model

For every in-scope comment the skill produces one record:

```yaml
- comment_id: <int>          # REST databaseId of the thread's first comment
  thread_id: <node id>       # GraphQL PullRequestReviewThread id (for resolve)
  path: <file>
  line: <int|null>           # null when GitHub has marked the comment outdated
  claim: <one-line restatement of what the comment asserts>
  validity: correct | incorrect | uncertain
  settle_state: fixed | rejected_by_author | open
  our_stance: agree | disagree | n/a   # our position on the author's rejection
  verified: true | false     # did WE confirm it at HEAD, not just trust a claim?
  evidence: <concrete: file:line at HEAD, a passing check, counts, a repro>
  action: { reply: true, react: <bool>, resolve: <bool> }
```

`validity`, `settle_state`, and `verified` are independent. The resolve
decision is a pure function of them (next section); it is never a guess.

## Action policy (resolve-when-verified)

The organizing principle is **help the author close settled threads**,
gated by **our own verification** so it never degrades into
rubber-stamping real bugs away.

| adjudication | reply | thumbs-up | resolve (default `verified_settled`) |
|---|---|:--:|---|
| correct + author **fixed** + **we verified** the fix at HEAD | yes -- "addressed in `<sha>`" | yes | **resolve** |
| correct + **still open / unaddressed** | yes -- confirm + direction | yes | leave open (author's to fix) |
| author **rejected with evidence** + **we agree** | yes -- second the refutation | no | **resolve** |
| **incorrect** + **we refute** with evidence | yes -- evidence | no | **resolve** |
| correct but author **dismissed it** + **we disagree** | yes -- push back, restate impact | no | leave open (do not paper over a real bug) |
| `uncertain` / needs human judgment | yes -- flag what to check | no | leave open |

Resolve fires **iff**:

```
resolve == (settle_state == fixed             && verified) ||
           (settle_state == rejected_by_author && our_stance == agree && verified) ||
           (validity == incorrect             && verified)
```

`verified` is load-bearing in every branch. No `verified`, no resolve --
reply only, leave the thread for a human. `resolve_policy` may *narrow*
this (e.g. `rejected_and_agree`) but may never drop the `verified`
requirement.

## Procedure

### Step 0 — Preconditions

1. `gh auth status` -- fail fast with a clear error if unauthenticated.
   Never fall back to `curl` + PAT (no credentials in skills, ever).
2. Parse `pr_ref` into `(owner, repo, number)`.
3. Resolve `mode`. If not explicitly `execute` (or a caller trust
   signal), set `mode = dry_run`. Record the decision in the result.
4. Resolve `comment_filter` (default `bots_only`) and `resolve_policy`
   (default `verified_settled`).

### Step 1 — Fetch PR state and enumerate threads

Pin the head/base SHAs and PR state (used as the "verified at HEAD"
reference and to detect drift before mutating):

```sh
gh api /repos/{owner}/{repo}/pulls/{N} \
  --jq '{head: .head.sha, base: .base.sha, state: .state, merged_at: .merged_at}'
```

Enumerate review threads with their resolution state and the first
comment's identity and body via GraphQL:

```graphql
query {
  repository(owner:"{owner}", name:"{repo}") {
    pullRequest(number:{N}) {
      reviewThreads(first:100) {
        nodes {
          id isResolved isOutdated
          comments(first:1) { nodes { databaseId author{login} path line originalLine body } }
        }
      }
    }
  }
}
```

Drop threads that are already `isResolved`. Apply `comment_filter`
(skip human-authored threads unless `all`/allowlisted). Skip threads
where our own prior reply marker is already present (§idempotency).

### Step 2 — Adjudicate each comment against HEAD (the verification gate)

For each in-scope comment, determine `validity`, `settle_state`, and --
critically -- whether **we** can verify the current state. Verification
means inspecting the code at the pinned HEAD, not trusting a "fixed"
claim in a reply:

- **Read the post-change file** at HEAD around the cited path/line and
  check whether the asserted problem is present.
- **Where a check exists** (a test, a script the comment is about),
  run it and use the result as evidence.
- **Cross-reference `verdict`** if supplied: an overlapping
  already-verified finding *is* the evidence; do not re-derive it.

Set `settle_state`:
- `fixed` if the code at HEAD no longer exhibits the issue (and ideally
  a commit/reply references it).
- `rejected_by_author` if the author replied keeping the current
  behavior and explaining why.
- `open` otherwise.

Set `verified = true` only when the skill itself produced the evidence
above. If the state cannot be verified deterministically, set
`validity = uncertain`, `verified = false` -- this routes to reply-only,
leave-open. **Never** set `verified = true` from an unverified author
claim.

### Step 3 — Decide actions

Apply the policy function from §Action policy to each record to fill
`action: { reply, react, resolve }`. Compose the reply text from the
adjudication (templates below). In `dry_run`, stop here and return the
adjudication table + the drafted reply bodies + the intended action per
thread. Mutate nothing.

Reply templates (adapt wording; keep it collaborative, evidence-first,
no mention of how the review was produced):

- `correct_fixed`: "Addressed in `<sha>`: <what changed>. Resolving."
- `correct_open`: "Confirmed -- <restate the issue and the concrete fix
  direction>." (no resolve)
- `incorrect`: "<evidence that refutes the claim, with concrete numbers
  / file:line>. Resolving."
- `rejected_agree`: "Agree with the author here -- <the evidence that
  makes the rejection correct>. Resolving."
- `rejected_disagree`: "<why the comment still stands and the impact if
  left>." (no resolve)
- `uncertain`: "Flagging for a human: <exactly what to check and why we
  could not verify it deterministically>." (no resolve)

### Step 4 — Build encoding-safe payloads

Each reply body is serialized as JSON and must survive `gh api` on
Windows (see Edge Cases):

1. Normalize smart punctuation in generated prose to ASCII
   (`—`→`--`, `’`→`'`, `“`/`”`→`"`, `…`→`...`). **Carve-out:** never
   normalize inside fenced/`suggestion` blocks -- replacement text must
   match source byte-for-byte.
2. Append the idempotency marker (last line of the body):
   `<!-- triage-existing-review-comments: comment_id={comment_id} -->`.
   `comment_id` is an integer, so the marker is injection-safe.
3. Write the `{ "body": ... }` JSON to a temp file as **UTF-8 without
   BOM** via
   `[System.IO.File]::WriteAllText($f, $json, (New-Object System.Text.UTF8Encoding($false)))`
   and pass it with `gh api --input <file>` (not stdin piping).

### Step 5 — Execute (only when `mode == execute`)

**Pre-mutation staleness recheck.** Re-fetch head SHA, base SHA, and
state; compare to Step 1. If head advanced, **re-run Step 2** for any
thread we intend to resolve (a fix or regression may have landed) --
do not resolve against a stale verification. If the PR closed/merged,
abort with `reason: pr_closed` / `pr_merged`.

Then, per thread, in this order:

1. **Reply:**
   `POST /repos/{owner}/{repo}/pulls/{N}/comments/{comment_id}/replies`
   with the prepared body file.
2. **Reaction** (when `action.react`):
   `POST /repos/{owner}/{repo}/pulls/comments/{comment_id}/reactions`
   with `content=+1`.
3. **Resolve** (when `action.resolve`): GraphQL
   `resolveReviewThread(input:{threadId:$thread_id})`. If it returns a
   permissions error, record `resolve: denied` for that thread and
   continue -- the reply still stands.

### Step 6 — Verify and report

- Re-query `reviewThreads`; confirm each intended resolve shows
  `isResolved: true` and each reply round-trips without mojibake
  (fetch the reply body back; repair per Edge Cases if needed).
- Emit the result:

```yaml
mode: dry_run | execute
threads_in_scope: <int>
replied: <int>
reacted: <int>
resolved: <int>
left_open: <int>          # open / contested / needs_human
resolve_denied: <int>     # permission failures
encoding_repaired: false | true
adjudications: [ <the §Adjudication model records> ]
```

## Edge Cases

### Windows / `gh api --input` UTF-8 mojibake

`gh api --input` on Windows can re-encode UTF-8 as cp1252 in transit
(`—` → `ΓÇö`, etc.), silently. Mitigate in order: (1) ASCII-normalize
generated prose (with the fenced-block carve-out); (2) write payloads
as UTF-8 without BOM; (3) after posting, fetch each reply back, detect
known mojibake sequences (`ΓÇö`, `ΓÇª`, `ΓÇÖ`, `ΓÇ£`, `ΓÇ¥`), and
`PATCH` the corrupted reply with cleaned text.

### Idempotency / re-runs

A thread already carrying our `<!-- triage-existing-review-comments:
comment_id=... -->` marker has been adjudicated; skip the reply. Re-runs
may still *resolve* a thread that became resolvable since the last run
(e.g. the author has since pushed the fix) -- the resolve step is keyed
on the live `isResolved` state, not on the marker.

### Human-authored threads

Default `bots_only` skips them. Even with `all`, treat human threads
conservatively: reply is fine, but only resolve on the
`fixed + verified` or `rejected + agree + verified` branches, and
prefer leaving a human's thread for them to close unless explicitly
asked.

### Author claims "fixed" but we cannot verify

Downgrade to `uncertain` / `verified=false` → reply flagging exactly
what to check, leave open. The verification gate is the whole point;
an unverifiable claim never resolves.

### Outdated / `isOutdated` comment (line is `null`)

The cited line no longer exists in the diff. Adjudicate against the
post-change file by symbol/region rather than line number; if the
concern was made moot by the change, that is a `fixed` state (resolve
on verification). If it cannot be located, `uncertain`.

### `gh` not authenticated / insufficient scope

Detect at Step 0. Replies need `repo` write; `resolveReviewThread`
needs triage/write. On a resolve permission error, keep the reply and
record `resolve: denied` rather than failing the whole run.

## Validation

After an `execute` run, the skill MUST verify:

- [ ] Every thread with `action.resolve` shows `isResolved: true`
      (or `resolve: denied` recorded with the API error).
- [ ] Every reply round-trips with no mojibake (fetched back).
- [ ] No thread was resolved with `verified == false` (invariant; a
      violation is a skill bug — fail the run and report).
- [ ] `dry_run` runs produced zero mutations (no replies, reactions,
      or resolves on the PR).

### Signal Capture

Emit one ALAS execution signal per run:

- `run_id` shared with an upstream `dual-model-review` run when invoked
  programmatically (correlates the verdict to the triage).
- Event type: `review_comments_triaged`.
- Outcome: `completed` (all in-scope threads adjudicated + actioned),
  `partial` (some resolves denied or encoding repaired), `failed`.
- Notes: `replied=N reacted=N resolved=N left_open=N denied=N mode=<mode>`.
- Self-assessment (5-key, aligned with the skill template):
  - `accuracy` (1-5): did each adjudication match the code at HEAD, and
    did we never resolve an actually-open thread?
  - `completeness` (1-5): were all in-scope threads adjudicated?
  - `skill_alignment` (1-5): did the run follow Steps 0-6 and honor the
    verification gate and `dry_run` default?
  - `developer_experience` (1-5): did the author get a cleaner thread
    list without losing a real issue?
  - `confidence` (1-5): confidence in the above.

> **Follow-up:** register `review_comments_triaged` in SIGNALS.md's
> catalog (track as a separate PR); until then it dispatches as a
> generic `skill_execution` envelope.

## Detection

N/A — this skill adjudicates and delivers; it consumes existing PR
comments and the code at HEAD and produces replies/reactions/resolves.
There is no vulnerability pattern to detect.

## Remediation

N/A — this skill does not change product code. Fixing a finding it
confirms is the author's job (or `chrysalis-pr-lifecycle`'s).

## Escalation

Reply-only and surface to caller (do not resolve) when:

- The thread cannot be verified deterministically (`uncertain`).
- The comment is correct but the author dismissed it -- keep it open
  and restate the impact; never resolve a contested real issue.
- `resolveReviewThread` returns a permissions error -- record
  `resolve: denied`, keep the reply.

Abort the run (do not mutate further) when:

- The PR closed or merged between Step 1 and the Step 5 recheck
  (`reason: pr_closed` / `pr_merged`).
- `gh` lacks the scope to comment at all (Step 0).

Stay in `dry_run` (mutate nothing) whenever the caller did not give an
explicit execute/trust signal -- adjudicating someone else's PR is a
visible action and the default is to show the plan first.

## Out of scope

- **Posting new findings.** That is `post-dual-review-as-pr-comments`.
- **Fixing the findings.** That is `chrysalis-pr-lifecycle` or a human.
- **Iterating a review.** This skill triages what is already on the PR
  once; it does not run review rounds.
- **Non-GitHub platforms.** Azure DevOps PR threads are a sibling skill.

## See also

- `plugins/dual-review/skills/post-dual-review-as-pr-comments/SKILL.md`
  — the outbound sibling (post *our* findings); shares the delivery
  machinery (gh auth, HEAD pin, UTF-8 payloads, thread resolution).
- `plugins/dual-review/agents/dual-model-review.md` — the verdict
  producer this skill can cross-reference.
- `plugins/dual-review/docs/dual-model-review-pattern.md` — why
  independence matters; the asymmetry insight.
