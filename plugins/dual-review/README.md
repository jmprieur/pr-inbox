# dual-review

Dual-model code review for high-stakes changes at trust boundaries, plus a
delivery skill that turns the review verdict into inline GitHub PR comments.

## Contents

| Kind | Name | Purpose |
|---|---|---|
| Agent | [`dual-model-review`](agents/dual-model-review.md) | Orchestrates one round of review by two independent reviewer models (default: Opus + GPT) over the same change set, then cross-references their findings into a single verdict + de-duplicated finding list. One round per call; the caller iterates. |
| Skill | [`post-dual-review-as-pr-comments`](skills/post-dual-review-as-pr-comments/SKILL.md) | Converts a `dual-model-review` verdict into a single GitHub PR review with inline comments anchored to the exact lines, suggestion blocks where the fix is mechanical, and a summary body. |
| Doc | [`dual-model-review-pattern`](docs/dual-model-review-pattern.md) | The iteration loop, the model-asymmetry insight that motivates N=2 reviewers, when to invoke, and provenance. |

## How they fit together

```mermaid
flowchart LR
    A[Change at a trust boundary] --> B[dual-model-review agent]
    B -->|YAML verdict| C[post-dual-review-as-pr-comments skill]
    C -->|inline comments + body| D[GitHub PR review]
```

The agent produces a structured verdict (convergent findings, unique-to-each
reviewer findings, disagreements, optional severity drift). The skill is the
canonical delivery path that posts it to the PR. Both reference the companion
pattern doc.

## Requirements

- The skill uses the GitHub CLI (`gh`) to read the PR diff and post reviews.

## Provenance

Adapted from the `security-toolkit` plugin in the 1ES `ai-plugins` marketplace.
