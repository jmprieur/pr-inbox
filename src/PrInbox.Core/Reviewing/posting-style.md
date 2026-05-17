# Posting style

Conventions for the review comments you'll write into `findings.yaml`.
The pr-inbox companion posts them to GitHub verbatim after I curate, so
the style they land in is the style they were written in.

## Location

- **Inline only.** Anchor every finding to its file path and the exact
  line in the diff (or the closest line for symbol-level findings).
  Don't write a PR-level summary comment — the inline anchors are the
  signal.

## Tone

- Collaborative, not authoritative.
- Prefer **questions over affirmations** when it fits — "Is this
  intentional?" / "Would [X] be cleaner here?" usually lands better
  than "This is wrong."
- Simple words. Say it once.
- Lean — short paragraphs, no preamble.
- Don't mention how the review was produced (no "dual-model review",
  no "model A said X, model B said Y", no "I was asked to look at
  this PR"). The comment stands on its own.

## Code suggestions

- Provide a GitHub ```` ```suggestion ```` block whenever the fix is
  concrete and small enough to drop in.
- **Keep suggestions minimal.** If the change is a one-line tweak,
  the suggestion is one line. Don't include unchanged context lines
  just to make the diff "look complete" — replacing 3 unchanged lines
  with the same 3 lines plus a 4th is noise.
- Skip the suggestion if the right answer is a question, or if the
  fix needs more context than fits in a single anchor.

## Severity calibration

Anchor levels so reviewers calibrate identically across sessions:

- **critical** — exploitable vulnerability, data loss, or a guaranteed
  production outage. Reach for this rarely.
- **high** — a clear bug or regression that will break a real user
  path. Wrong logic, off-by-one in a hot path, missing null check on
  user input, a contract violation a caller relies on.
- **medium** — correctness or maintainability issue that's likely to
  bite but not guaranteed. Missing test coverage of a tricky branch,
  a subtle race that hasn't manifested, a confusing API that invites
  misuse.
- **low** — naming, dead code, minor refactor, doc polish. Worth
  saying once, not worth blocking on.

If unsure between two tiers, pick the lower one. Reviewers earn trust
by not crying wolf.
