-- pr-inbox initial schema (v1)
--
-- All timestamps are stored as ISO-8601 UTC strings (TEXT, e.g. "2026-05-13T22:01:13Z").
-- All foreign-key references point at pull_requests.pr_identity (the display id).
-- The stable_identity column holds the platform-id-based durable key for joins
-- across repo/project renames.

PRAGMA foreign_keys = ON;

-- ---------------------------------------------------------------------------
-- Schema version table. Migrations are inserted in monotonic version order;
-- a startup check asserts the table reflects every embedded migration.
-- ---------------------------------------------------------------------------
CREATE TABLE schema_version (
  version       INTEGER PRIMARY KEY,
  name          TEXT    NOT NULL,
  applied_at    TEXT    NOT NULL
);

-- ---------------------------------------------------------------------------
-- The current-row truth for every PR ever seen. Never hard-deleted; status
-- and tracking_reason capture lifecycle. Look up by pr_identity (display) or
-- stable_identity (durable across renames).
-- ---------------------------------------------------------------------------
CREATE TABLE pull_requests (
  pr_identity                       TEXT PRIMARY KEY,        -- gh.com:owner/repo#N
  stable_identity                   TEXT NOT NULL UNIQUE,    -- platform-id-based
  source_id                         TEXT NOT NULL,           -- gh.com | ghe.<host> | ado:<org>
  source_kind                       TEXT NOT NULL,           -- github | github-enterprise | azure-devops
  display_repo                      TEXT NOT NULL,
  number                            INTEGER NOT NULL,
  title                             TEXT,
  author_login                      TEXT,
  url                               TEXT NOT NULL,
  status                            TEXT NOT NULL,           -- open | closed | merged | inaccessible
  tracking_reason                   TEXT NOT NULL,           -- assigned | previously_assigned | manually_added | archived
  identity_used                     TEXT NOT NULL,           -- which of my identities sees this PR
  first_seen_at                     TEXT NOT NULL,
  last_synced_at                    TEXT NOT NULL,
  last_briefed_head_sha             TEXT,                    -- HEAD when brief.md was last generated
  last_review_run_head_sha          TEXT,                    -- HEAD when dual-model-review last ran
  last_posted_review_head_sha       TEXT                     -- HEAD when I last posted a review (v0.2+)
);

CREATE INDEX idx_pull_requests_status       ON pull_requests(status);
CREATE INDEX idx_pull_requests_tracking     ON pull_requests(tracking_reason);
CREATE INDEX idx_pull_requests_last_synced  ON pull_requests(last_synced_at);
CREATE INDEX idx_pull_requests_source       ON pull_requests(source_id);

-- ---------------------------------------------------------------------------
-- Append-only snapshot of platform state at each successful per-PR fetch.
-- A new row is inserted only if any tracked field changed since the previous
-- snapshot; otherwise the sync just bumps pull_requests.last_synced_at.
-- ---------------------------------------------------------------------------
CREATE TABLE pr_snapshots (
  id                    INTEGER PRIMARY KEY,
  pr_identity           TEXT    NOT NULL REFERENCES pull_requests(pr_identity),
  synced_at             TEXT    NOT NULL,
  head_sha              TEXT    NOT NULL,
  base_sha              TEXT    NOT NULL,
  merge_base_sha        TEXT,
  ordered_commit_shas   TEXT    NOT NULL,                    -- JSON array, newest-first
  reviewer_state        TEXT,                                -- requested | approved | changes_requested | dismissed
  pr_state              TEXT    NOT NULL,                    -- platform-native status (open/closed/merged/abandoned)
  raw_metadata_json     TEXT
);

CREATE INDEX idx_pr_snapshots_pr_identity ON pr_snapshots(pr_identity, synced_at DESC);

-- ---------------------------------------------------------------------------
-- Threads observed on a PR (review comments, issue comments, ADO threads,
-- review bodies). first_seen_at + last_seen_at + resolved_at let us compute
-- "what's new since my last review" without storing transient counters.
-- ---------------------------------------------------------------------------
CREATE TABLE observed_threads (
  id                    INTEGER PRIMARY KEY,
  pr_identity           TEXT    NOT NULL REFERENCES pull_requests(pr_identity),
  platform_thread_id    TEXT    NOT NULL,
  kind                  TEXT    NOT NULL,                    -- review_comment | issue_comment | review_body | ado_thread
  author_login          TEXT,
  is_bot                INTEGER NOT NULL DEFAULT 0,
  bot_kind              TEXT,                                -- copilot-review | copilot-coding-agent | github-actions | other
  first_seen_at         TEXT    NOT NULL,
  last_seen_at          TEXT    NOT NULL,
  resolved_at           TEXT,
  raw_json              TEXT,
  UNIQUE(pr_identity, platform_thread_id)
);

CREATE INDEX idx_observed_threads_pr      ON observed_threads(pr_identity);
CREATE INDEX idx_observed_threads_bot     ON observed_threads(pr_identity, is_bot);
CREATE INDEX idx_observed_threads_unread  ON observed_threads(pr_identity, last_seen_at);

-- ---------------------------------------------------------------------------
-- Immutable review-run records. Each `pr-inbox review <id>` invocation
-- creates one row + an on-disk directory containing brief.md + metadata.json.
-- Re-running review always appends a new row, never overwrites.
-- ---------------------------------------------------------------------------
CREATE TABLE review_runs (
  id                    INTEGER PRIMARY KEY,
  pr_identity           TEXT    NOT NULL REFERENCES pull_requests(pr_identity),
  created_at            TEXT    NOT NULL,
  brief_path            TEXT    NOT NULL,                    -- absolute path to brief.md
  run_directory         TEXT    NOT NULL,                    -- absolute path to run dir
  head_sha              TEXT    NOT NULL,
  base_sha              TEXT    NOT NULL,
  status                TEXT    NOT NULL,                    -- generated | session_started | abandoned | superseded
  copilot_session_id    TEXT,
  notes                 TEXT
);

CREATE INDEX idx_review_runs_pr ON review_runs(pr_identity, created_at DESC);

-- ---------------------------------------------------------------------------
-- v0.2+ tables that exist from day 1 so writer code can be added without
-- migrations. v0.1 never inserts here.
-- ---------------------------------------------------------------------------
CREATE TABLE posted_reviews (
  id                    INTEGER PRIMARY KEY,
  pr_identity           TEXT    NOT NULL REFERENCES pull_requests(pr_identity),
  review_run_id         INTEGER REFERENCES review_runs(id),
  platform_review_id    TEXT    NOT NULL,
  posted_at             TEXT    NOT NULL,
  head_sha_at_post      TEXT    NOT NULL,
  identity_used         TEXT    NOT NULL,
  inline_count          INTEGER NOT NULL DEFAULT 0,
  body_present          INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX idx_posted_reviews_pr ON posted_reviews(pr_identity, posted_at DESC);

-- ---------------------------------------------------------------------------
-- Per-(source, identity) sync attempt log. `list` reads the most recent row
-- for each source to surface partial-failure staleness.
-- ---------------------------------------------------------------------------
CREATE TABLE sync_runs (
  id              INTEGER PRIMARY KEY,
  source_id       TEXT    NOT NULL,
  identity_used   TEXT    NOT NULL,
  started_at      TEXT    NOT NULL,
  completed_at    TEXT,
  status          TEXT    NOT NULL,                          -- ok | partial | failed | rate_limited | running
  error           TEXT,
  prs_seen        INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX idx_sync_runs_source ON sync_runs(source_id, started_at DESC);
