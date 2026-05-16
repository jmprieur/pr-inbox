-- Migration 006: brief dossier columns
--
-- Adds the data the review brief needs but never had: PR body (author's
-- framing), file list with +/-, CI / mergeable state, and per-thread
-- anchor (file:line) + comment body excerpt. SQLite ALTER TABLE only
-- supports ADD COLUMN, so every new column is nullable / has a default.

ALTER TABLE pull_requests ADD COLUMN body TEXT;

ALTER TABLE pr_snapshots ADD COLUMN mergeable_state TEXT;
ALTER TABLE pr_snapshots ADD COLUMN ci_status TEXT;
ALTER TABLE pr_snapshots ADD COLUMN files_json TEXT;

ALTER TABLE observed_threads ADD COLUMN last_comment_body TEXT;
ALTER TABLE observed_threads ADD COLUMN anchor_path TEXT;
ALTER TABLE observed_threads ADD COLUMN anchor_line INTEGER;
