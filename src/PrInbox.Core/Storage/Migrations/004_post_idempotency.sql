-- Migration 004: per-finding idempotency for posted_reviews.
--
-- Why: chunk 7 (review publisher) needs to detect "I already posted this
-- finding from this run" so retries/double-clicks/concurrent sessions
-- don't duplicate comments. The original posted_reviews row only tracks
-- the platform review id, not which findings it contained.
--
-- New columns:
--   finding_ids_json          — JSON array of finding ids (e.g. ["f01","f03"])
--                               as written by the dual-model-review agent.
--                               IDs may not be stable across regenerated
--                               runs, so we also store fingerprints.
--   finding_fingerprints_json — JSON array of "<file>|<line>|<title-sha1>"
--                               strings. Stable-ish fallback when finding
--                               ids drift.
--   review_url                — html_url returned by the platform (GitHub
--                               review url or ADO thread url).
--   dry_run                   — always 0 in this column; we keep it so
--                               future debug/log rows for dry-runs could be
--                               added without another migration. Default 0.
--
-- Backfill: existing rows had no per-finding tracking, so they keep the
-- empty array defaults. The publisher's idempotency check sees them as
-- "no findings recorded; assume nothing posted from this row" — which is
-- safer than skipping new posts based on absent data.

ALTER TABLE posted_reviews
  ADD COLUMN finding_ids_json TEXT NOT NULL DEFAULT '[]';

ALTER TABLE posted_reviews
  ADD COLUMN finding_fingerprints_json TEXT NOT NULL DEFAULT '[]';

ALTER TABLE posted_reviews
  ADD COLUMN review_url TEXT;

ALTER TABLE posted_reviews
  ADD COLUMN dry_run INTEGER NOT NULL DEFAULT 0;
