-- Migration 003: progressive fetch — add enrich_state to pull_requests.
--
-- A PR row is created in tier-2 (fast) sync with enrich_state='basic'. It
-- moves to 'enriched' once tier-3 sync has fetched the per-PR detail and
-- threads. Future fast syncs that detect upstream change (LastUpdated >
-- LastSyncedAt) downgrade the row back to 'basic' so the next enrich pass
-- refreshes it.
--
-- Backfill: every existing row is marked 'basic'. We don't infer 'enriched'
-- from snapshot existence because old sync code wrote snapshots before
-- threads — a failed sync could have left a row with a snapshot but no
-- threads, and that row should re-enrich.

ALTER TABLE pull_requests
  ADD COLUMN enrich_state TEXT NOT NULL DEFAULT 'basic';

CREATE INDEX idx_pull_requests_enrich_state
  ON pull_requests(source_id, enrich_state);
