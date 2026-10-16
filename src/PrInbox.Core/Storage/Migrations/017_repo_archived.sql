ALTER TABLE pull_requests ADD COLUMN is_repo_archived INTEGER NOT NULL DEFAULT 0;

-- Repository archival need not change the PR's updated timestamp.
-- Re-enrich cached GitHub PRs once to populate the new flag.
UPDATE pull_requests
SET enrich_state = 'basic'
WHERE source_kind IN ('github', 'github-enterprise')
  AND status = 'open';
