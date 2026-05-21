-- Migration 010 — Per-PR "I've reviewed this" marker.
--
-- Why: a user who reviews a PR (in the GitHub UI, in `gh pr review`, or
-- via this app's publisher) wants the row to disappear from the inbox
-- until the author pushes again. The existing `is_ignored` flag is too
-- broad — it hides the PR forever until the user un-ignores it. We want
-- a softer "snooze until head changes" semantic.
--
-- Mechanism:
--   * marked_done_head_sha — the SHA the user marked as done at.
--   * marked_done_at        — when the marker was applied.
--
-- A row is treated as "done" (and hidden by default) when
--   marked_done_head_sha = (latest pr_snapshot).head_sha
-- i.e. the author has NOT pushed a new commit since the user marked it
-- done. Once the head SHA changes, the row reappears automatically
-- ("Updated since you marked done").
--
-- The sync upsert path deliberately leaves these columns alone — only
-- the dedicated MarkDoneAsync / ClearDoneAsync methods write them.
--
-- Backfill: NULL on every existing row — no PRs are marked done at
-- migration time, which is the correct default.

ALTER TABLE pull_requests ADD COLUMN marked_done_head_sha TEXT;
ALTER TABLE pull_requests ADD COLUMN marked_done_at TEXT;
