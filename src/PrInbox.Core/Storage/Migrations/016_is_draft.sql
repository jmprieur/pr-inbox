-- Migration 016: draft PR flag
--
-- Promotes the draft bit to a first-class column so the UI can distinguish
-- work-in-progress PRs (your own drafts in My PRs, draft PRs you're asked to
-- review). Previously the draft state was only buried in pr_snapshots'
-- raw_metadata_json and never surfaced.
--
-- Populated from the list tier on Azure DevOps (its PR-list returns isDraft)
-- and from the enrich tier on GitHub/GHE (Octokit's search Issue does not
-- expose draft; PullRequest.Get does). Like the other "latest observation"
-- flags it defaults to 0 and is refreshed on enrich.

ALTER TABLE pull_requests ADD COLUMN is_draft INTEGER NOT NULL DEFAULT 0;

CREATE INDEX idx_pull_requests_is_draft ON pull_requests(is_draft);
