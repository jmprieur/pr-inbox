-- Migration 005: post-v1 UX improvements.
--
-- 1. is_ignored — UI-level per-PR hide flag. Toggled via the inbox
--    "Ignore" button. Combined with the config IgnoredRepos regex
--    list as the "Show ignored" filter target. Data is still synced;
--    this is purely a presentation filter.
--
-- 2. disappeared_at — set by the disappeared-diff sweep when a PR
--    that we'd been tracking as open drops out of the source's
--    review-inbox query AND a follow-up enrichment still reports
--    status='open'. Indicates the user is no longer a requested
--    reviewer (or the search criteria moved on). UI hides these by
--    default; cleared if the PR reappears in a future fast pass.
--
-- 3. last_swept_at — when the TTL sweep last re-enriched the row.
--    Used to pick the oldest-swept candidates for each cycle so no
--    open row stays unchecked forever.
--
-- 4. ui_preferences — single-key, single-value store for inbox
--    toggle states. Keys (currently):
--      inbox.show_closed     -- "true"/"false"
--      inbox.show_ignored    -- "true"/"false"
--      inbox.source_filter   -- JSON array, e.g. ["EMU","public"]
--    Callers serialize non-string values to JSON.

ALTER TABLE pull_requests
  ADD COLUMN is_ignored INTEGER NOT NULL DEFAULT 0;

ALTER TABLE pull_requests
  ADD COLUMN disappeared_at TEXT;

ALTER TABLE pull_requests
  ADD COLUMN last_swept_at TEXT;

CREATE INDEX idx_pull_requests_is_ignored ON pull_requests(is_ignored);

CREATE TABLE ui_preferences (
  key        TEXT PRIMARY KEY,
  value      TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
