---
name: post-dual-review-as-pr-comments
description: >-
  Posts a dual-model-review verdict as inline GitHub PR review comments
  with suggestion blocks where the fix is mechanical. Verifies every
  finding's file:line resolves to a diff-hunk anchor; reroutes
  non-anchorable findings to the review body. Chooses the review
  event from the verdict.
metadata:
  author: pr-inbox maintainers
  version: "0.1.0"
  category: review
  severity: medium
  triggers:
    - "dual-model-review verdict needs to be delivered as PR comments"
    - "Operator: 'post the review on the PR'"
    - "Caller agent has a CRITICALS_FOUND / FINDINGS_FOUND / CONVERGED verdict"
---

# Post Dual-Review as PR Comments

> **Purpose:** Convert a structured `dual-model-review` verdict into a
> single GitHub Pull Request review with inline comments anchored to
> the exact lines that need to change, and a summary body. Codifies
> what was previously orchestrator folklore (verifying anchors,
> generating suggestion blocks, working around platform encoding
> quirks, choosing the review event).

## Context

`dual-model-review` emits a YAML verdict object describing convergent
findings, unique-to-each-reviewer findings, disagreements, and (if
present) `severity_drift`. The verdict is _data_; turning it into a
useful artifact on the PR is _delivery_, and the GitHub PR Reviews
API has enough sharp edges that this work needs its own skill rather
than being re-invented in every caller.

This skill is the canonical delivery path. It sits **after**
`dual-model-review` (or any other producer of a finding-list payload
that conforms to the same shape) and **before** human review on the
PR.

## When to use

- A caller (agent or human) just ran `dual-model-review` and wants
  the findings posted on the PR rather than printed in chat.
- Findings need to be anchored at exact `file:line` so reviewers can
  see them in context, not as a flat appendix.
- The fix is small enough on at least some findings that a GitHub
  `suggestion` block is the right format (one-click apply).

## When NOT to use

- The verdict was `CONVERGED` and the caller is happy not to post
  anything visible on the PR. (You can still emit an `APPROVE` review
  with empty `comments[]`; that's a caller decision.)
- The findings are about the PR description, branch protection, or
  anything not in a file diff. Use a regular issue comment instead.
- You don't have permission to post reviews on the target repo.

## Inputs

| Input | Description |
|---|---|
| `pr_ref` | PR identity. One of: `owner/repo#N`, full URL, or `(owner, repo, number)` triple. |
| `verdict` | The structured `dual-model-review` output (the YAML object from §6 of that agent). The skill reads `verdict.run_id`, `verdict.round`, `verdict.head_sha`, `verdict.base_sha` (optional), and `verdict.verdict` as load-bearing fields. `verdict.run_id` is **required and shape-validated**: Step 0 refuses with `malformed_verdict` if absent, empty, whitespace-only, longer than 128 chars, or contains any character outside the allowlist `[A-Za-z0-9_.:-]` (UUID v4 / ULID / dotted-composite compatible). The allowlist is mechanically necessary because Step 5 interpolates `run_id` raw into an HTML-comment dedup marker; a producer-controlled `-->` would otherwise escape the marker and inject Markdown into the review body. `verdict.round` must be a non-negative integer (same marker-escape rationale). `verdict.head_sha` must be exactly 40 lowercase hex chars; Step 1 compares it against the live PR head and refuses with `stale_verdict` on mismatch (force-push detection). `verdict.base_sha` is optional; if present it must also be 40 lowercase hex chars, and Step 1 refuses with `stale_verdict_base` on mismatch (base-advance detection). `verdict.verdict` must be one of the §6 enum literals (`CONVERGED`, `CRITICALS_FOUND`, `DISAGREEMENT`, `FINDINGS_FOUND`, `INCOMPLETE`); a typo or out-of-enum value is `malformed_verdict`. Step 0 also enforces verdict↔buckets cross-field consistency (e.g. `CRITICALS_FOUND` requires ≥1 high/critical finding in `agreed_findings ∪ unique_to_a ∪ unique_to_b ∪ disagreements`). (Earlier drafts allowed an "older producer fallback" that disabled idempotency or accepted any non-empty string; both paths were removed.) |
| `event` | Optional override of the review event. Default: derived from `verdict.verdict` per the table below. **Cannot override** an `INCOMPLETE` verdict to a posting event — see §Escalation. |
| `body_preamble` | Optional text prepended to the auto-generated summary body. |
| `auth` | Caller-provided GitHub auth (typically `gh` CLI session). The skill uses `gh api`. |

### Default `event` selection

| `verdict.verdict` | Default `event` |
|---|---|
| `CRITICALS_FOUND` | `REQUEST_CHANGES` |
| `DISAGREEMENT` | `COMMENT` (caller decides) |
| `FINDINGS_FOUND` | `COMMENT` |
| `CONVERGED` | `APPROVE` (only if caller explicitly opts in; otherwise skip posting) |
| `INCOMPLETE` | _do not post_ — return error to caller. **The `event` input cannot override this** (see §Escalation). |

The caller can override the default event for any verdict EXCEPT
`INCOMPLETE` — `INCOMPLETE` means at least one reviewer was missing
or the prior-findings sanitization refused, and posting a public
review action against partial / zero reviewer data would deceive
maintainers about what actually ran. The default is otherwise a
recommendation, not a policy.

## Procedure

### Step 0 — Precondition: validate structure, then refuse on `INCOMPLETE`

Before any other work (no diff fetch, no anchor verification, no
event resolution), evaluate the following guards **in order**.
Each guard returns immediately on first match — later guards are
not evaluated. Structural validation (`malformed_verdict`) runs
**before** the INCOMPLETE soft-refusal so that adversarially-edited
or partially-conforming verdicts cannot reach Step 1+ on the soft
codepath.

1. **`verdict.run_id` shape.** Refuse with `reason: malformed_verdict,
   message: "verdict.run_id missing or wrong shape"` if any of:
   - missing, `null`, empty, or not a string
   - whitespace-only after trim
   - longer than 128 characters
   - contains any character outside `[A-Za-z0-9_.:-]` (this allowlist
     covers UUID v4, ULID, and reasonable composite identifiers, and
     by construction excludes `-->`, newlines, control characters,
     and Markdown metacharacters that would otherwise let a producer
     inject content into the rendered review body when `run_id` is
     interpolated into the HTML-comment dedup marker at Step 5).

   This guard runs first because Step 5's marker (`<!--
   post-dual-review-as-pr-comments: run_id={verdict.run_id} round=
   {verdict.round} -->`) interpolates `run_id` raw, and a
   producer-controlled `run_id` containing `-->` would close the
   HTML comment early, leak attacker-controlled bytes as visible
   Markdown, and may also break dedup matching on retry. The
   producer schema (§6) requires UUID v4 / ULID / collision-resistant
   shape; this allowlist is the **mechanically enforced** floor on
   that contract — without it the §6 requirement is paper-only.

2. **`verdict.round` shape.** Refuse with `reason: malformed_verdict`
   if `verdict.round` is missing or not a non-negative integer.
   Same rationale: `round` is also interpolated raw into the marker.

3. **`verdict.verdict` enum membership.** Refuse with
   `reason: malformed_verdict, message: "verdict.verdict not in §6
   enum"` if `verdict.verdict` is missing, not a string, or not
   equal to one of the literal strings `CONVERGED`, `CRITICALS_FOUND`,
   `DISAGREEMENT`, `FINDINGS_FOUND`, `INCOMPLETE`. Without this
   guard, a typo (`"FINDINGS"` missing `_FOUND`) or a string-equality
   dodge (`"INCOMPLETE_BUT_OK"`) silently bypasses the INCOMPLETE
   soft-refusal check at guard 6 (which uses `==` against the literal
   `"INCOMPLETE"`) and reaches Step 1+ with no default event mapping
   — undefined behavior on a public review surface. The catch-all
   guard 8 is *not* sufficient because "missing required fields" is
   commonly read as "absent" rather than "wrong-enum-value".

4. **`verdict.head_sha` shape.** Refuse with `reason: malformed_verdict,
   message: "verdict.head_sha missing or wrong shape"` unless
   `verdict.head_sha` is a string of exactly 40 lowercase hex
   characters (`[0-9a-f]{40}` — the canonical Git SHA-1 form). This
   is required because Step 1 will compare it against the live
   PR's `head.sha` to detect a force-push between verdict generation
   and post (see Step 1's stale-verdict check). Missing or
   wrong-shape `head_sha` means stale-verdict detection cannot
   function, so we refuse rather than silently post a
   `dual-model-review` finding on a different commit's code.

   **`verdict.base_sha` shape (conditional).** PR #61 §6 declares
   `base_sha` as RECOMMENDED (not REQUIRED). If `verdict.base_sha`
   is present it MUST also be `[0-9a-f]{40}`; refuse with
   `reason: malformed_verdict, message: "verdict.base_sha wrong
   shape"` if a non-conformant value was emitted. If `verdict.base_sha`
   is absent, do NOT refuse here — fall through to Step 1's degraded
   stale-base policy below. This split (REQUIRED head_sha,
   RECOMMENDED base_sha) is intentional: head movement is a
   force-push and always invalidates anchors; base movement only
   shifts hunk geometry on the same head and is detectable from the
   live PR object even without producer cooperation, so missing
   `base_sha` is a degraded-but-recoverable state, not a hard
   refusal.

5. **Cross-field `not_run` invariant.** If `reviewer_a.outcome ==
   "not_run"` or `reviewer_b.outcome == "not_run"`, the only valid
   `verdict.verdict` per the producer's §5 verdict table is
   `INCOMPLETE`. Otherwise refuse with `reason: malformed_verdict,
   message: "reviewer outcome 'not_run' requires verdict==INCOMPLETE;
   got <verdict.verdict>"`.

6. **`INCOMPLETE` structural conformance.** If
   `verdict.verdict == "INCOMPLETE"`, require the §6 `incomplete_reason`
   block: it must be present, `incomplete_reason.code` must be one
   of the §6 enum values (`reviewer_timeout`, `reviewer_error`,
   `prior_findings_rejected`, `malformed_input`),
   `incomplete_reason.message` must be a non-empty string, and
   `incomplete_reason.missing` must be a list whose elements are
   each one of the literal strings `"reviewer_a"` or `"reviewer_b"`
   (no duplicates, length 0–2). Furthermore the `missing` list MUST
   be consistent with the actual `not_run` outcomes, with a carve-out
   for the input-rejection codepath:
   - **Forward direction (always):** every name in `missing` MUST
     correspond to a reviewer with `outcome == "not_run"`. Listing
     a reviewer who actually produced a `greenlight` or `findings`
     outcome is malformed.
   - **Reverse direction (conditional):** if `incomplete_reason.code`
     is `reviewer_timeout` or `reviewer_error`, then every reviewer
     with `outcome == "not_run"` MUST appear in `missing`. If
     `incomplete_reason.code` is `prior_findings_rejected` or
     `malformed_input`, the reverse direction is NOT enforced —
     producer §6 explicitly allows `missing: []` in those cases
     because no reviewer was ever spawned, so "missing" relative
     to a successful pair is the wrong frame; both reviewers are
     `not_run` but the cause is input rejection, not reviewer
     unavailability. Without this carve-out, the consumer would
     falsely reject every `prior_findings_rejected` /
     `malformed_input` verdict as `malformed_verdict`.

   If any of these fail, refuse with
   `reason: malformed_verdict, message: "verdict==INCOMPLETE
   requires structured incomplete_reason per §6 and consistent
   missing/not_run mapping"`. This drops the earlier "older producer
   fallback" carve-out: the post-PR-#61 schema requires the block,
   and a verdict claiming `INCOMPLETE` without it is malformed, not
   soft. The `missing` shape check is required because Step 0 guard
   7 propagates the value verbatim to the caller and Step 4's
   defensive `not_run` renderer reads it as a list.

7. **Verdict ↔ findings-buckets cross-field consistency.**
   The top-level `verdict.verdict` enum value must be supported by
   the emitted finding/disagreement buckets. The producer schema
   (§6 of `dual-model-review`) emits shared findings in
   `agreed_findings` (with per-finding `severity`); there is no
   separate `agreed_criticals` bucket — that name was retired in
   PR #61, and the C/H subset is obtained by filtering
   `agreed_findings` by `severity ∈ {critical, high}`.
   - `CRITICALS_FOUND` ⇒ at least one finding of severity `critical`
     or `high` MUST appear in `agreed_findings`, `unique_to_a`,
     `unique_to_b`, or `disagreements` (severity check applied to
     each bucket's per-finding `severity` field). An empty
     bucket-union under `CRITICALS_FOUND` would otherwise drive the
     default-event mapping `CRITICALS_FOUND ⇒ REQUEST_CHANGES`
     (see §3) into a blocking review with zero actionable inline
     comments.
   - `FINDINGS_FOUND` ⇒ at least one finding of any severity MUST
     appear in the bucket-union above, AND no finding may be
     `critical`/`high` (those would have been classified
     `CRITICALS_FOUND`).
   - `DISAGREEMENT` ⇒ `disagreements` bucket MUST be non-empty.
   - `CONVERGED` ⇒ both reviewer outcomes MUST be `greenlight`
     AND all four finding/disagreement buckets (`agreed_findings`,
     `unique_to_a`, `unique_to_b`, `disagreements`) MUST be empty.
   - `INCOMPLETE` ⇒ no bucket-level invariant beyond guard 6's
     `incomplete_reason` requirements.

   Severity comparisons are against the lowercase literals from §6
   (`critical|high|medium|low`); a producer emitting `Critical` or
   `HIGH` would be caught upstream as a malformed finding shape,
   not silently miss this guard.

   On any violation refuse with `reason: malformed_verdict,
   message: "verdict.verdict <X> inconsistent with finding buckets"`.

8. **`INCOMPLETE` refusal (soft).** If guards 1–7 passed and
   `verdict.verdict == "INCOMPLETE"`, **refuse** with
   `posted: false, reason: incomplete_verdict, incomplete_reason:
   <verdict.incomplete_reason.code>, message:
   <verdict.incomplete_reason.message>, missing:
   <verdict.incomplete_reason.missing>`. Do **not** proceed to Step 1
   even if the caller supplied an `event` override. The override
   path explicitly does not apply to `INCOMPLETE` (see §Escalation).

9. **Generic structural fallback.** If the verdict is otherwise
   structurally invalid (missing required fields per §6), refuse
   with `reason: malformed_verdict`.

This check is the first thing the skill does. It is not optional
and it has no override flag in this skill's contract — callers who
genuinely need to post a partial-reviewer artifact must do so via a
different mechanism (e.g. a regular PR comment with explicit
"reviewer Y timed out" wording) so the false "Independent review by
A and B" framing is never rendered.

### Step 1 — Fetch the diff and pin head + base SHAs and PR state

```sh
gh api /repos/{owner}/{repo}/pulls/{N} \
  --jq '{head: .head.sha, base: .base.sha, state: .state, merged_at: .merged_at}'
gh api /repos/{owner}/{repo}/pulls/{N}.diff > pr.diff   # PWD-relative; cross-platform
```

> Implementation note: prior drafts used `/tmp/pr.diff`, which doesn't
> exist on Windows. Use a workspace-relative path or
> `$env:TEMP` / `$TMPDIR` if you must place it outside PWD.

You need five things:
- The **head SHA** so the review is anchored to a specific commit.
  Capture it as `head_sha_at_fetch` and reuse it as `commit_id` in
  Step 5; re-check immediately before POST (see Step 5).
- The **base SHA**, captured as `base_sha_at_fetch`. The base can
  advance independently of the head (e.g. an automated merge of
  `main` into the PR base), shifting hunk geometry without changing
  the head SHA. Step 5 rechecks both.
- The **PR state** (`state` ∈ {`open`, `closed`} and `merged_at`
  ∈ {timestamp, `null`}), captured as `state_at_fetch` and
  `merged_at_fetch`. A merged or closed PR keeps head SHA, base SHA,
  and diff bytes identical — none of those signals catch a merge or
  close mid-flow. **If `state != "open"` OR `merged_at != null` at
  Step 1**, refuse to proceed with `reason: pr_not_open` unless the
  caller passes an explicit `force_post_on_closed=true` opt-in.
  Posting `REQUEST_CHANGES` on shipped code is a stale demand the
  producer can't act on.
- The **unified diff** so you can verify each finding's anchor.
  Keep the bytes; Step 5 recompares the recheck diff against this
  one.

**Stale-verdict check (fail closed on head; conditional on base).**
Compare `head_sha_at_fetch` against `verdict.head_sha` (validated in
Step 0 guard 4). If they differ, **refuse** with `reason:
stale_verdict, message: "verdict was generated against
<verdict.head_sha>; PR head is now <head_sha_at_fetch>; force-push
detected"`. This catches the window between when `dual-model-review`
analyzed the PR and when this skill runs: if the author force-pushes
in that window, the verdict's `(file, line)` anchors may resolve to
lines that exist in the current diff but contain entirely different
code, and inline comments would be posted on innocent code.

Then, if `verdict.base_sha` is present (RECOMMENDED per PR #61 §6),
compare it against `base_sha_at_fetch` from the live PR object. If
they differ, refuse with `reason: stale_verdict_base, message:
"verdict was generated against base <verdict.base_sha>; PR base is
now <base_sha_at_fetch>; base advanced (e.g. main merged into PR
target) — diff geometry has shifted"`. This closes the
verdict-generation→Step-1 window where the PR base advanced (an
auto-merge of `main` into the PR's target branch) without changing
the head SHA, silently shifting which lines are `+` vs context in
the diff and turning previously-anchorable findings into
mis-anchored ones. If `verdict.base_sha` is absent, log a degraded
note (`base_sha_unverifiable: true` in the result) and proceed —
PR #61 schema allows this, and Step 5's recheck still catches
within-skill base movement.

The skill MUST NOT silently degrade to "post anyway" on head
mismatch — re-running the review is cheap and correct; posting
stale findings on rewritten code is expensive and wrong. A caller
who genuinely wants to post against a force-pushed head must pass
`force_post_on_stale_head=true` AND have refreshed the verdict.
The same `force_post_on_stale_head=true` opt-in covers
`stale_verdict_base` (a base advance is a strict subset risk of a
head force-push for the consumer's purposes); a separate
opt-in is overkill.

### Step 2 — Verify anchors

For every finding in `agreed_findings`, `unique_to_a`, `unique_to_b`,
and `disagreements`:

1. Parse the diff. For each `diff --git` block, identify the
   post-change file from the `+++ b/<path>` header (handles renames
   and copies cleanly). Build a map of anchorable
   `(file, line) -> hunk_id` entries — the post-change line number
   of every `+` line and every unchanged context line inside
   `@@ ... @@` hunks, tagged with the index of the hunk that
   contains it (e.g. `(file, 0)`, `(file, 1)`, …). **Retain the
   hunk_id**: Step 3's same-hunk strict rule for multi-line source
   spans cannot be verified without it. With `gh api .../pulls/{N}.diff`
   default context (-U3) hunks rarely abut, but `-U0` or pathological
   diffs CAN produce adjacent hunks where every interior post-line
   is anchorable yet a multi-line range still crosses a boundary;
   the hunk_id is the only signal that catches that case.
2. **Cited `line` is always a post-change line number.** Reviewers
   read post-change files; pre-change line numbers cited for deleted
   lines are not anchorable.
3. **Edge cases — all `false`:**
   - File appears as `+++ /dev/null` (deletion).
   - Binary diff (`Binary files ... differ`, no hunks).
   - Cited line is the hunk header line itself (`@@ -10,5 +10,6 @@`).
   - Cited line is a `-` line (does not exist post-change).
   - Cited file isn't in the diff at all (no `+++ b/<path>` for it).
4. **Single-line check:** if `(finding.file, finding.line)` is in
   the map, keep as inline.
5. **Multi-line check:** if `finding.start_line` is present and
   `< finding.line`, additionally require that EVERY line from
   `start_line` through `line` inclusive maps to the SAME `hunk_id`
   for `finding.file`. If any line is missing or maps to a
   different hunk, the multi-line anchor cannot be posted — fall
   back per Step 3 (drop to single-line at `finding.line` if that
   alone is anchorable, else promote to prose).
6. Else (no anchor possible): **promote to body**. Append the
   finding's text to the review body under a
   `### Findings on unchanged code` section, with an explicit
   `(in {file} near line {line})` citation. Do not silently drop —
   those are often the most interesting findings.

> Note: GitHub also accepts `start_line` + `line` for a multi-line
> anchor. **Strict rule:** `start_line`, `line`, **and every line
> between them** must lie within **one and the same** `@@ ... @@`
> hunk. A range that crosses a hunk boundary — even if both endpoints
> are individually anchorable in different hunks — returns HTTP 422
> "Line could not be resolved." If you cannot satisfy the
> single-hunk constraint, drop back to a single-line anchor or to
> prose in the body.

If the verdict's reviewers populated `diff_anchorable`, treat it as
a **hint, not ground truth** — re-verify against the actual diff.
Reviewers reason from post-change files and frequently mis-flag
hunk membership. (The orchestrator-computed value should be
correct, but defense in depth is cheap.)

### Step 3 — Generate suggestion blocks

A finding qualifies for a GitHub `suggestion` block when **all** of:

- `finding.fix` describes a mechanical edit (replace this token,
  delete this line, change this literal).
- The replacement spans ≤ 3 lines.
- The replacement is fully self-contained (does not require new
  imports, new helpers, or coordinated edits elsewhere).

For qualifying findings, format the comment body as:

````markdown
**[severity]** scenario sentence.

**Fix:** prose direction, optional reference to spec/CWE.

```suggestion
<exact replacement text for the line(s) being commented on>
```
````

For non-qualifying findings, omit the suggestion block and write
prose only. Do not synthesize a suggestion block from `fix` text
that isn't mechanical — guessed code in a one-click-apply UI is a
trap.

**Source-span vs replacement-text.** GitHub's `suggestion` block
**replaces the source range covered by the comment anchor** with
the replacement text — the two are independent dimensions:

- The **source span** is the range of existing lines being
  replaced. It comes from the finding (`finding.start_line` ..
  `finding.line` if `start_line` is provided; otherwise just the
  single line `finding.line`).
- The **replacement text** is what goes inside the
  ` ```suggestion ` fence. Its line count is independent of the
  source span — a single source line may be replaced by a
  multi-line replacement, and a multi-line source span may be
  replaced by zero lines (deletion).

**Use a multi-line anchor (`start_line` + `line` + `start_side` +
`side`) iff the SOURCE span covers more than one line.** That is,
the trigger is `finding.start_line < finding.line`, not the
replacement text's line count. The same-hunk strict rule from
Step 2 applies to the source span: every line from `start_line`
through `line` inclusive must lie within one and the same
`@@ ... @@` hunk. If you cannot satisfy that constraint, drop
back to prose in the body.

If the producer's verdict does not include `start_line` for a
finding, treat the source span as a single line at `finding.line`
and use a single-line anchor. The replacement text inside the
fence may still span multiple lines; GitHub will, on Apply,
replace just that one source line with the multi-line replacement
text — which is the correct behavior when the intent is "expand
this one line into several."

**Fence-length rule (CommonMark §4.5).** If the replacement text
itself contains backtick runs of length _k_, the surrounding
` ```suggestion ` fence must use at least _k+1_ backticks.
Default to 3 backticks; bump to 4+ on demand. Forgetting this
breaks the suggestion silently — the closing fence appears in the
rendered review and the suggestion never registers as applicable.

**Body-content fences.** The outer code block in the rendered
comment uses 4 backticks (` ```` `) so that an inner
` ```suggestion ` (3 backticks) parses cleanly. If `finding.fix`
prose itself contains 4-backtick runs, escalate the outer fence
length further.

### Step 4 — Build the review body

```markdown
{body_preamble (if provided)}

## Dual-Model Review -- Round {round}

**Verdict: {verdict}**

{if both reviewer_a.outcome and reviewer_b.outcome are in
{greenlight, findings} (i.e. neither is `not_run`):}
Independent review by `{reviewer_a.model}` and `{reviewer_b.model}`,
spawned in parallel. {one-line synthesis}
{else — INCOMPLETE was caught at Step 0, so this branch is
defensive only; if rendering ever reaches it, render the honest
form using verdict.incomplete_reason.missing to identify which
reviewer responded:}
⚠ This review is incomplete. Only `{whichever reviewer responded}`
returned a usable outcome; `{other reviewer}` did not (`outcome:
not_run`). The single reviewer's findings follow.

### Convergent findings ({len(agreed_findings)})

{numbered list, one line each, with file:line and severity}

### Unique to {reviewer_a.model} ({len(unique_to_a)})
{list — these are exhaustive-enumeration wins}

### Unique to {reviewer_b.model} ({len(unique_to_b)})
{list — these are lateral-pattern wins}

### Disagreements ({len(disagreements)})
{list — same code, conflicting conclusions; human decides}

### Severity drift
{if reviewers systematically diverged on severity, summarize here.
Not the same as disagreements; document the calibration delta.}

### Findings on unchanged code
{findings promoted from inline because their line wasn't in any hunk
— sins of omission, contradictions with surrounding unchanged context,
findings on `-`/deleted lines.}

### Additional findings (inline-quota exceeded)
{findings that ARE diff-anchorable but were demoted to the body
because Step 5's inline-comment cap was hit. These DO cite changed
lines; they were just below the severity cutoff for inline.}

### Suggested next-round prompt steer
{verdict.suggested_next_prompt_steer, if populated}

---
_Generated by `post-dual-review-as-pr-comments` from a
`dual-model-review` verdict._
```

### Step 5 — Post the review

**Pre-POST staleness recheck.** Immediately before serializing the
payload, re-fetch head SHA, base SHA, PR state, merged_at, and the
diff:

```sh
gh api /repos/{owner}/{repo}/pulls/{N} \
  --jq '{head: .head.sha, base: .base.sha, state: .state, merged_at: .merged_at}'
gh api /repos/{owner}/{repo}/pulls/{N}.diff > pr.diff.recheck
```

Compare against the values captured in Step 1
(`head_sha_at_fetch`, `base_sha_at_fetch`, `state_at_fetch`,
`merged_at_fetch`, original `pr.diff`). The head SHA alone is not
sufficient: if the **base** branch advances (e.g. an automated
merge of `main` into the PR base) while head SHA stays constant,
the PR's effective diff geometry can shift — hunks split, merge,
or change line numbers — and anchors verified against the stale
diff may now resolve to different context or fail with HTTP 422.
Equally, a merged or closed PR keeps head SHA, base SHA, and diff
bytes identical, so SHA-only comparison misses a mid-flow merge.

**Abort and surface to caller** with a distinct reason if any of:

- `head` SHA changed → `reason: head_advanced`,
- `base` SHA changed → `reason: base_advanced`,
- `state` flipped from `open` → `reason: pr_closed`,
- `merged_at` flipped from `null` to a timestamp → `reason: pr_merged`,
- the recheck diff is not byte-identical to the Step 1 diff →
  `reason: diff_drifted`.

The caller decides whether to re-run the whole pipeline against
the new state or post against the old state with an explicit
`force_stale` override (note: `force_stale` does NOT override
`pr_merged` or `pr_closed` — those require the separate
`force_post_on_closed=true` opt-in introduced in Step 1). Do not
silently re-anchor.

**Idempotency / retry.** Read `run_id` and `round` from
`verdict.run_id` and `verdict.round` (NOT separate skill inputs —
see §Inputs); both have already been shape-validated by Step 0
guards 1–2. If a prior run for the same `(pr_ref, verdict.run_id,
verdict.round)` already posted a review, do not post again.

Detect prior posts by enumerating **all** reviews (not just the
first page) and matching the run-id marker hidden in the body.
`gh api`'s `--paginate` flag is required because the default
`per_page=30` returns only the first page, but `--paginate`
**alone is not sufficient**: per `gh help api`, "Each page is a
separate JSON array or object. Pass `--slurp` to wrap all pages
of JSON arrays or objects into an outer JSON array." Without
`--slurp` (or equivalent per-page processing), parsing the
combined stdout as a single JSON document fails on page 2+ —
two adjacent JSON arrays — and a literal implementation will
either error out or silently search only page 1, reintroducing
the duplicate-post hole on PRs with >100 reviews. Use one of
these two parse-safe forms:

```bash
# Form A — slurp all pages into a single outer array, then scan.
# Match the FULL marker (run_id AND round AND closing ` -->`) so a
# short run_id (e.g. "1") cannot collide with another marker that
# happens to contain "run_id=12345 ..." as a substring. The Step 0
# allowlist for run_id excludes '>', '<', and whitespace, so the
# producer cannot forge the closing ` -->` token inside a run_id.
gh api --paginate --slurp \
  "/repos/{owner}/{repo}/pulls/{N}/reviews?per_page=100" \
  | jq -r --arg m "<!-- post-dual-review-as-pr-comments: run_id=<verdict.run_id> round=<verdict.round> -->" \
      '.[][] | select(.body | contains($m)) | .id'

# Form B — stream per-page; jq is applied to each page independently.
gh api --paginate \
  "/repos/{owner}/{repo}/pulls/{N}/reviews?per_page=100" \
  --jq '.[] | select(.body | contains("<!-- post-dual-review-as-pr-comments: run_id=<verdict.run_id> round=<verdict.round> -->")) | .id'
```

The dedup scan MUST enumerate all review pages before concluding
the marker is absent. A scan that only checks page 1 is a
duplicate-post bug, not a partial implementation.

Embed a stable marker in the review body so retries are detectable:

```markdown
<!-- post-dual-review-as-pr-comments: run_id={verdict.run_id} round={verdict.round} -->
```

The Step 0 `run_id` allowlist (`[A-Za-z0-9_.:-]`, ≤128 chars)
guarantees this marker cannot be broken by `-->`, newlines, or
Markdown metacharacters in `run_id`. `verdict.run_id` is required
by the producer schema (§6) and validated in Step 0; if it was
missing or malformed, Step 0 already refused with
`malformed_verdict` and we never reach this step.

A retry that finds this marker should report
`posted: false, reason: already_posted, existing_review_id: <id>`
to the caller.

Build the JSON payload:

```json
{
  "commit_id": "<head SHA, re-confirmed above>",
  "body": "<from step 4, with run_id marker>",
  "event": "<from defaults table or override>",
  "comments": [
    {
      "path": "<file>",
      "line": <int>,
      "side": "RIGHT",
      "body": "<from step 3>"
    },
    {
      "path": "<file>",
      "start_line": <int>,
      "start_side": "RIGHT",
      "line": <int>,
      "side": "RIGHT",
      "body": "<from step 3, with multi-line suggestion>"
    }
  ]
}
```

The first comment shape is single-line; the second is multi-line.
**Use the multi-line shape iff `finding.start_line` is present and
`finding.start_line < finding.line` — i.e. the SOURCE span covers
more than one line (see Step 3).** Replacement-text length must not
drive anchor shape: a single source line replaced by a multi-line
suggestion uses the single-line shape; a multi-line source span
replaced by a single line (or zero lines) uses the multi-line shape.

**Comment count cap.** If `len(comments) > 50`, the API may reject
or silently truncate. Split into one review with the top-50 by
severity (inline) and promote the remainder to the body's
`### Additional findings (inline-quota exceeded)` section
(**not** `### Findings on unchanged code` — quota-overflow findings
are anchorable; they were just truncated for budget, and mixing
them with non-anchorable findings would mislead a maintainer who
skims that section as "out of scope"). Cite each with explicit
`(in {file} line {line})`. Record this in the result as
`truncated: true, dropped_inline: <int>`.

POST it:

```sh
gh api -X POST /repos/{owner}/{repo}/pulls/{N}/reviews --input payload.json
```

### Step 6 — Verify and report

- Capture the returned `html_url` and report it to the caller.
- Fetch the posted review body **and every inline comment** back
  (`gh api /repos/{owner}/{repo}/pulls/{N}/reviews/{review_id}` for
  the body; `gh api /repos/{owner}/{repo}/pulls/{N}/comments?per_page=100`
  paginated for the inline comments belonging to this review_id).
  Check **each** comment body for mojibake (see edge cases) — not
  just the first. The fenced/`suggestion` carve-out for ASCII
  normalization in Step 5 means the first comment can be ASCII-clean
  while a later comment's suggestion fence corrupts; verifying only
  the first would silently ship corrupted suggestions that, when
  applied, write corrupted source. Re-encode (PATCH) any corrupted
  comments, or fail the whole delivery to the caller if repair is
  not possible.

```yaml
posted: true
review_id: <int>
review_url: <url>
inline_comments: <int>
promoted_to_body: <int>     # findings whose anchor wasn't in a hunk
dropped_inline: <int>       # findings demoted by Step 5 quota cap
suggestion_blocks: <int>
encoding_repaired: false | true
event: REQUEST_CHANGES | COMMENT | APPROVE
```

## Edge Cases

### Windows / `gh api --input` UTF-8 mojibake

`gh api --input <file>` on Windows can re-encode UTF-8 content as
cp1252 in transit, turning `—` (U+2014) into `ΓÇö` and similar for
other smart-punctuation glyphs. The corruption is silent and only
visible after fetching the posted content back.

**Mitigations, in order of preference:**

1. **Avoid the issue.** Replace smart-punctuation in generated text
   with ASCII equivalents before serialization: `—` → `--`, `’` →
   `'`, `“` → `"`, `”` → `"`, `…` → `...`. The review summary loses
   no information; mechanical post-processing is reliable.
   **Carve-out:** never apply this normalization inside fenced code
   blocks (` ``` ... ``` `, including `suggestion` fences) — the
   replacement text in a `suggestion` block must match the
   post-change source byte-for-byte, and ASCII-fying smart quotes
   inside it will make the suggestion no longer match (Apply will
   fail) or silently corrupt the user's source if applied. Detect
   fence regions during normalization and skip them.
2. **Write the payload file as UTF-8 without BOM** using
   `[System.IO.File]::WriteAllText($path, $json, (New-Object System.Text.UTF8Encoding($false)))`.
   This helps but has not always been sufficient on observed setups.
3. **Detect-and-repair after posting.** Fetch the posted body via
   `gh api /repos/.../pulls/{N}/reviews/{id}` and `/comments/{id}`,
   detect known mojibake sequences (`ΓÇö`, `ΓÇª`, `ΓÇÖ`, `ΓÇ£`,
   `ΓÇ¥`), and `PATCH` each comment + `PUT` the review body with
   the cleaned text. The corrupted bytes round-trip cleanly through
   PATCH because they're now plain ASCII byte sequences.

The skill SHOULD do (1) by default and SHOULD do (3) as a verify
step. (2) is a backstop.

### Non-anchorable findings outnumber anchorable ones

If more than half the findings promote to body, this is a smell —
the diff may be tiny relative to the surface area being reviewed,
and the verdict is mostly about omissions. Still post the review,
but include in `body_preamble`: _"Most findings concern unchanged
code; consider whether this PR should be split or whether reviewers
were operating on the right scope."_

### `commit_id` mismatch (post-POST)

The pre-POST recheck in Step 5 should catch the common case. As a
backstop, after posting, fetch head SHA once more; if it advanced
between Step 5 and now, log a warning in the result
(`head_advanced_during_post: true`) — the comments still anchored
to the SHA we posted with, but reviewers should know the diff
moved.

### No findings (CONVERGED + caller wants to APPROVE)

Post a review with empty `comments[]`, `event: APPROVE`, and a
short body explaining the dual-model basis. Skip steps 2 and 3.

### `gh` not authenticated

Detect via `gh auth status` before Step 1. Fail fast with a clear
error; do not attempt to post via `curl` with a PAT (no credentials
in skills, ever).

## Validation

After posting, the skill MUST verify:

- [ ] `gh api /repos/.../pulls/{N}/reviews/{id}` returns 200 and
      the body matches what was sent (modulo encoding repair).
- [ ] Number of inline comments returned == number sent.
- [ ] No comment has `line: null` (would indicate GitHub silently
      dropped the anchor — should be impossible after Step 2 but
      verify).
- [ ] If any encoding repair was needed, log the exact substitutions
      so the failure mode is captured for skill improvement.

### Signal Capture

Emit one ALAS execution signal per run with:

- `run_id` shared with the upstream `dual-model-review` run if
  invoked programmatically (correlates verdict to delivery).
- Event type: `dual_review_posted`.
- Outcome: `completed` (review posted), `partial` (posted but some
  findings dropped or encoding repaired), `failed` (post failed).
- Notes: compact key=value summary including
  `inline=N promoted=N suggestions=N encoding_repaired=<bool> event=<event>`.
- Self-assessment (5-key, aligned with skill template):
  - `accuracy` (1-5): do the inline anchors land on the lines the
    findings actually concern, and do `suggestion` blocks compile/
    match the source they replace?
  - `completeness` (1-5): were any findings dropped silently
    (anchor failure not promoted, encoding loss, etc.)?
  - `skill_alignment` (1-5): did the skill follow Steps 1-6 in
    order, or did it short-circuit?
  - `developer_experience` (1-5): did one round suffice, or did
    the caller have to retry due to skill bugs?
  - `confidence` (1-5): how confident is the skill in the above
    self-rating?

The signal flows through traceline per
`plugins/security-toolkit/quality/signal-capture/SIGNALS.md`.

> **Follow-up:** the `dual_review_posted` event type is currently
> not registered in SIGNALS.md's catalog. Track this as a separate
> SIGNALS.md PR; in the meantime the event will dispatch as a
> generic `skill_execution` envelope.

## Detection

N/A — this skill is review/delivery-orchestration-focused. There is
no vulnerability pattern to detect; the skill consumes a structured
verdict from an upstream agent and converts it into a GitHub
artifact.

## Remediation

N/A — this skill _delivers_ findings into a PR conversation; it
does not remediate code. Remediation of the findings themselves is
handled by `chrysalis-pr-lifecycle` or a human author.

## Escalation

Refuse and surface to caller (do not retry blindly) when:

- `verdict.verdict == "INCOMPLETE"` — the skill refuses at Step 0
  before any GitHub work happens. There is no `event` override
  that permits posting on an `INCOMPLETE` verdict, because the
  default body template's "Independent review by A and B" framing
  would be a lie when only one reviewer responded. Callers who
  genuinely want to surface a partial-reviewer artifact should do
  so via a regular comment with explicit "reviewer Y did not
  respond" wording, not via this skill.
- PR is not open at fetch time (Step 1) and caller did not pass
  `force_post_on_closed=true` — refuse with `reason: pr_not_open`.
- PR closed or merged between Step 1 and the Step 5 recheck —
  abort with `reason: pr_closed` or `reason: pr_merged`. The
  `force_stale` override does not cover these.
- `gh api` returns 422 on a comment after Step 2 verified the
  anchor — indicates the diff drifted or our hunk-parser has a bug.
  Surface the failing finding and the API response.
- `event: APPROVE` is requested but the verdict had any non-empty
  finding bucket — refuse and ask the caller to confirm.
- `event` and `verdict.verdict` disagree in a direction that lowers
  signal (e.g. caller forces `APPROVE` on `CRITICALS_FOUND`) — warn
  and require explicit override flag.

## Out of scope

- **Iterating on the verdict.** This skill posts what it's given. If
  the verdict needs another round, that's the caller's job.
- **Fixing the findings.** That's `chrysalis-pr-lifecycle` or a
  human.
- **Posting to systems other than GitHub.** Azure DevOps PR threads
  use a different API; that would be a sibling skill.

## See also

- `plugins/dual-review/agents/dual-model-review.md` — the
  producer of the verdict this skill consumes.
- `plugins/dual-review/docs/dual-model-review-pattern.md` —
  why N=2 reviewers, why independence matters.
