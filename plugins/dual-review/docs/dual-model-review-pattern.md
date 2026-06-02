# Dual-Model Review Pattern

> **Status:** Working hypothesis, N=2.
> **Companion to:** [`agents/dual-model-review.md`](../agents/dual-model-review.md)
> **Provenance:** PR #47 (honesty-architecture, the original observation),
> PR #50 (reviewer-reply-validator, the convergence run that motivated this agent).

## Problem

When AI generates code at a **trust boundary** — a validator that
gates what gets posted, a parser that determines which inputs are
honored, a remediation patch that closes a CWE — single-model review
(by the same model that wrote the code, or by a single different
model) leaves predictable blind spots. The reviewer that catches
exhaustive enumeration cases misses lateral pattern matches. The
reviewer that thinks laterally misses the boring corner of the
specification.

Manual one-pass review by a human is too slow to iterate, and humans
under time pressure exhibit the same blind-spot pattern (different
ones, but no smaller).

## Pattern

**Critique-then-fix loop driven by two independent reviewers in
different model families, run to convergence.**

```
   ┌──────────┐
   │  change  │
   └────┬─────┘
        │
        ▼
   ┌─────────────────┐    ┌─────────────────┐
   │ Reviewer A      │    │ Reviewer B      │   (parallel, independent;
   │ (e.g. Opus)     │    │ (e.g. GPT)      │    different model families)
   └────────┬────────┘    └────────┬────────┘
            │                      │
            └──────────┬───────────┘
                       ▼
              ┌────────────────┐
              │  Cross-ref     │   verdict:
              │  & synthesize  │   - CONVERGED (greenlight from both)
              └────────┬───────┘   - CRITICALS_FOUND
                       │           - DISAGREEMENT
                       │           - INCOMPLETE
                       ▼
              ┌────────────────┐
              │   Caller       │   if CRITICALS_FOUND: fix, then loop
              │   decides      │   if CONVERGED: ship (subject to human gate)
              └────────────────┘
```

The orchestration of one round is encoded in
[`agents/dual-model-review.md`](../agents/dual-model-review.md). The
caller iterates; the agent does not.

## The asymmetry insight

Across the two observations we have, the two reviewer model families
fail in **structurally different** ways. This is the property the
pattern relies on.

| Property | "Reviewer A" (Opus family observed) | "Reviewer B" (GPT family observed) |
|---|---|---|
| Strength | Exhaustive enumeration of the formal spec | Lateral pattern matching across abstraction layers |
| Catches well | BOM, CRLF, lazy continuation, escape sequences, blockquote/list/setext interactions, schema corner fields, ordering invariants | HTML blocks at the wrong abstraction layer, related-but-distinct attack surfaces, "did you also consider this *other* element class," mismatched assumptions between two adjacent files |
| Misses | The "wait, but what about that *adjacent* surface?" class | The boring spec edge cases inside the named surface |
| Pace | Slow, methodical, long ruled-out lists | Fast, intuitive, short but high-leverage findings |

**This is why two reviewers from the same family is not a substitute
for two reviewers from different families.** In the two runs we
have, same-family redundancy would not have covered the
complementary blind spot — both Opuses would likely have missed the
lateral case the GPT pass caught, both GPTs would likely have missed
the spec corners the Opus pass caught. The hypothesis we're working
from is that *different model families* provide more structural
diversity than *more reviewers from the same family*. That hypothesis
is supported by N=2 observations, not proven; treat the categorical
"would" claims in the table above as observed tendency, not law.

> **Re-evaluate this table when model families ship major versions.**
> The strengths and misses above are pinned to the Opus 4.x and
> GPT-5.x generations as observed in early-to-mid 2026. A future
> Opus 5 or GPT-6 may shift the asymmetry — same family, different
> failure shape — or collapse it (both reviewers behaving like
> "Reviewer A," for example), in which case "different families"
> stops buying the structural diversity this pattern depends on.
> Treat each new generation as a fresh N=1 and update the table
> rather than carrying forward stale categorizations.

## When to invoke

Worth the cost (≈3-5 min wall, two model invocations per round, plus
human time interpreting):

- **Validators that gate trust boundaries.** Reviewer-reply validator,
  artifact validators, schema validators with security consequences.
- **Parsers of adversary-controlled input.** Markdown, YAML, JSON
  with custom semantics, anything that will be fed input from a PR
  branch or comment field.
- **Remediation patches in security-toolkit skills.** Where "fix
  introduces a new vulnerability" is a real failure mode.
- **Agent prompts that gate other agents' behavior.** Misdirection
  here cascades.
- **Schemas with security implications.** Where "field X is optional"
  vs "field X is required" determines whether a forgery passes.

Not worth the cost:

- **Style, naming, formatting changes.** Both reviewers will hand
  back nits and call it done. Use a linter.
- **Pure refactors with full test coverage.** The tests are the
  reviewer.
- **Documentation-only PRs.** Unless the documentation *is* the
  trust boundary (e.g. agent prompts).
- **Trivially-mechanical fixes.** Bumping a dependency version with
  a clean diff — the existing CI is enough.

## The 6-step iteration loop (caller's job)

The agent does step 2-3 for one round. The caller does the rest, and
loops.

1. **Scope.** State `{{CONTEXT}}` and `{{KNOWN_INVARIANTS}}`
   precisely. Vague scope produces vague findings.
2. **Spawn two reviewers in parallel** (the agent does this).
3. **Cross-reference** (the agent does this) → verdict +
   `suggested_next_prompt_steer`.
4. **Triage.** Real bugs → fix. Stylistic noise → discard. Surfaced
   disagreements → think.
5. **Fix and verify locally.** Run the tests. Add a regression test
   for every accepted critical.
6. **Loop.** If `CRITICALS_FOUND`, run another round with
   `{{PRIOR_FINDINGS}}` populated. If `CONVERGED`, ship subject to
   human gate.

## Round-prompt evolution

The caller's job between rounds is to evolve the prompt so reviewers
look for *other* classes than the ones already fixed. Without this,
the second round just re-flags the same surface and convergence
becomes confirmatory rather than adversarial.

The agent supports this with the `suggested_next_prompt_steer` field
in its output. The caller should pass that, plus a structured
summary of what was fixed, into `{{PRIOR_FINDINGS}}` for the next
call.

**Open question:** whether the prompt-steer heuristic generalizes
beyond CommonMark / transparency-architecture domains. We have N=2
data points; both showed the pattern. The third invocation in a new
domain is the test.

## What CONVERGED is and is not evidence of

**Is** evidence of:

- Two structurally-different reviewer models did not see a critical
  in this round.
- The named invariants are likely satisfied for the inputs both
  reviewers considered.
- The change has crossed a higher bar than single-model review.

**Is not** evidence of:

- "No bugs exist." There may be a class of bug neither reviewer
  thought to look for.
- "Human review can be skipped." CODEOWNERS approval is the gate;
  this agent makes that gate cheaper to clear, not optional.
- "The reviewers' analytic models are correct." Both reviewers
  could share a wrong assumption (e.g. about the runtime, the
  consumer of the output, the threat model). Two-from-same-family
  amplifies this; two-from-different-families reduces it but does
  not eliminate it.

## Cost analysis

Observed on PR #50 (the convergence run that motivated this agent):

- 6 review rounds total (some dual-model, some single-reviewer
  confirming passes), across which the caller iterated 5 fix
  commits before reaching greenlight.
- ~50 min wall total agent time. Most of that was the caller fixing
  and re-running tests, not the reviewers themselves.
- ~10 reviewer-model invocations across the run.
- Result: 36/36 tests green, every dual-model round surfaced ≥1 real
  critical the prior code passed manual reasoning. Round 4 Opus
  would have green-lit code GPT broke (HTML blocks). Round 5 GPT
  would have green-lit code Opus broke (Type 7 + inside-section).

### Per-round order of magnitude

A representative single round on a moderate-sized PR (≈500 lines
changed, ≈30 files of repo context) consumed roughly:

| Per reviewer per round | Input tokens | Output tokens |
|---|---|---|
| Opus 4.7 (`code-review` agent) | ~30k–50k | ~3k–8k |
| GPT-5.5 (`code-review` agent) | ~30k–50k | ~3k–8k |

So a **single dual-model round** is on the order of **60k–100k input
+ 6k–16k output tokens** total across both reviewers, plus the
caller's own context. Dollar cost varies with the provider's
current premium-tier pricing — check the live pricing pages rather
than relying on a number baked into this doc, which will age
faster than the rest of the content.

These figures are PR-#50-shaped: a tightly-scoped trust-boundary
PR with a focused diff. A larger PR (more files, deeper repo
context) scales input tokens proportionally; output tokens scale
with how many findings each reviewer enumerates, not with diff
size. The "when to invoke" decision is governed by the trust
boundary, not by the cost — but the cost is bounded enough that
"is this worth dual review?" rarely turns on the dollar figure.

The cost is real but bounded; the value is at the trust boundary
where a single missed bypass is worse than 6 rounds of review.

## Provenance honesty

This pattern is supported by **two** concrete observations to date:

- **PR #47** — the original honesty-architecture / transparency-
  architecture work for autonomous-agent output artifacts. First
  time the dual-model loop surfaced bugs neither single-model pass
  would have caught.
- **PR #50** — reviewer-reply-validator implementation. Five rounds
  of fix-iteration before convergence; documented in the PR
  description.

Two data points is not a law. Treat this document and the agent as
working hypotheses. Every invocation either confirms the asymmetry
pattern or weakens it; both outcomes are useful data and should be
captured. An ALAS-style execution signal — i.e., the
self-assessment / outcome / rationale signals already used elsewhere
in the security-toolkit (see `remediation-agent.md` and
`chrysalis-adversarial-review.md` for prior art) — on the
dual-model-review agent's run is the right place to record those
data points.

## See also

- [`agents/dual-model-review.md`](../agents/dual-model-review.md) — the agent itself.
- `chrysalis-adversarial-review` (in the upstream security-toolkit) —
  related but different: a single adversarial reviewer that does not
  read the fixer's reasoning. Adversarial-review is about
  *independence from the fixer*; dual-model-review is about
  *independence between two reviewers*. The two compose: a
  dual-model run can itself be the reviewer pair feeding an
  adversarial loop.
- PR #47, PR #50 — the two data points behind this pattern.
