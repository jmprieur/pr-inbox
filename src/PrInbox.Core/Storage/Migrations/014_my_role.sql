-- Adds the "my_role" dimension to pull_requests: the authenticated user's
-- role on a PR (reviewer | author | both), orthogonal to status and
-- tracking_reason. Enables the separate "My PRs" (authored) view without
-- polluting the reviewer inbox.
--
-- Why orthogonal instead of a new tracking_reason value: a PR can be both
-- authored and reviewed by the user, and the reviewer lifecycle
-- (assigned -> previously_assigned -> archived) is a different axis from
-- "what am I to this PR." Author-only rows carry tracking_reason =
-- 'not_reviewer' (the reviewer lifecycle does not apply to them); the
-- reviewer disappear-sweep skips them because they are never 'assigned'.
--
-- Backfill: every pre-existing row was discovered via the reviewer-only
-- inbox queries, so the DEFAULT 'reviewer' is correct for all of them.

ALTER TABLE pull_requests ADD COLUMN my_role TEXT NOT NULL DEFAULT 'reviewer';
--   values: 'reviewer' | 'author' | 'both'

CREATE INDEX idx_pull_requests_my_role ON pull_requests(my_role);
