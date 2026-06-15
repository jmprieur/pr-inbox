-- Migration 015: aggregate review decision on snapshots
--
-- Adds the PR-level review decision (the aggregate of other people's reviews:
-- approved / changes_requested) so the "My PRs" view can show "who approved /
-- changes requested" for PRs the user authored. This is distinct from
-- reviewer_state, which is the *self* review state used by the reviewer inbox.
--
-- Like the other dossier columns (ci_status, mergeable_state), it is a
-- "latest observation" field: nullable, populated on enrich, and updated in
-- place by the backfill path rather than forcing a new snapshot row.

ALTER TABLE pr_snapshots ADD COLUMN review_decision TEXT;
--   values (GitHub): 'approved' | 'changes_requested' | NULL (none / review required)
