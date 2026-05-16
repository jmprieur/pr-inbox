-- pr-inbox migration 002: URL as primary identifier + multi-identity source bindings.
--
-- Change of *semantics* (not schema shape): pr_identity now holds the canonical
-- PR URL (https://...) instead of the legacy display string (gh.com:owner/repo#N).
-- The url column already stored the URL, so this migration is a backfill UPDATE
-- that aligns pr_identity = url everywhere. The column name stays "pr_identity"
-- to avoid rebuilding 5 tables for FK changes.
--
-- Adds pr_source_bindings: a junction so that one PR (one URL) discovered by
-- multiple (source_id, identity) pairs lives as a single row in pull_requests
-- with multiple binding rows here. Enables seeing a PR once even when it shows
-- up via both github.com/jmprieur and github.com/jmprieur_microsoft.

-- ---------------------------------------------------------------------------
-- Phase 1: align child tables to point at pull_requests.url. The EXISTS guard
-- makes the update idempotent (re-running is a no-op; fresh DBs already match).
-- ---------------------------------------------------------------------------
UPDATE pr_snapshots
SET pr_identity = (
  SELECT pr.url
  FROM pull_requests pr
  WHERE pr.pr_identity = pr_snapshots.pr_identity
)
WHERE EXISTS (
  SELECT 1 FROM pull_requests pr
  WHERE pr.pr_identity = pr_snapshots.pr_identity
    AND pr.pr_identity <> pr.url
);

UPDATE observed_threads
SET pr_identity = (
  SELECT pr.url
  FROM pull_requests pr
  WHERE pr.pr_identity = observed_threads.pr_identity
)
WHERE EXISTS (
  SELECT 1 FROM pull_requests pr
  WHERE pr.pr_identity = observed_threads.pr_identity
    AND pr.pr_identity <> pr.url
);

UPDATE review_runs
SET pr_identity = (
  SELECT pr.url
  FROM pull_requests pr
  WHERE pr.pr_identity = review_runs.pr_identity
)
WHERE EXISTS (
  SELECT 1 FROM pull_requests pr
  WHERE pr.pr_identity = review_runs.pr_identity
    AND pr.pr_identity <> pr.url
);

UPDATE posted_reviews
SET pr_identity = (
  SELECT pr.url
  FROM pull_requests pr
  WHERE pr.pr_identity = posted_reviews.pr_identity
)
WHERE EXISTS (
  SELECT 1 FROM pull_requests pr
  WHERE pr.pr_identity = posted_reviews.pr_identity
    AND pr.pr_identity <> pr.url
);

-- ---------------------------------------------------------------------------
-- Phase 2: align parent. After this, pr_identity = url for every row.
-- ---------------------------------------------------------------------------
UPDATE pull_requests SET pr_identity = url WHERE pr_identity <> url;

-- ---------------------------------------------------------------------------
-- Phase 3: junction table for multi-identity-per-host PR dedupe.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS pr_source_bindings (
  pr_identity     TEXT    NOT NULL REFERENCES pull_requests(pr_identity),
  source_id       TEXT    NOT NULL,
  identity_used   TEXT    NOT NULL,
  discovered_at   TEXT    NOT NULL,
  PRIMARY KEY (pr_identity, source_id, identity_used)
);

CREATE INDEX IF NOT EXISTS idx_pr_source_bindings_pr ON pr_source_bindings(pr_identity);

-- Seed from existing rows. The current (source_id, identity_used) of each
-- pull_requests row becomes its initial binding. Future syncs will INSERT OR
-- IGNORE additional bindings for the same URL.
INSERT OR IGNORE INTO pr_source_bindings(pr_identity, source_id, identity_used, discovered_at)
SELECT pr_identity, source_id, identity_used, first_seen_at
FROM pull_requests;
