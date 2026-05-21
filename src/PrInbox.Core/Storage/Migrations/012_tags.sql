-- Migration 012 — User-defined tags (colored labels) per PR.
--
-- Why: a flag star answers "do I care about this PR?" but it's a single
-- axis. Users want to *organize* their inbox into clusters — by
-- workstream, by topic, by priority. Tags give that, and the inbox can
-- then group rows by tag (collapsible sections) so an 80-row inbox
-- becomes navigable.
--
-- Model:
--   * tags        — the dictionary. Name is the primary key
--                   (case-insensitive via COLLATE NOCASE). Color is a
--                   hex string the UI picks from a preset palette but
--                   the column accepts any string for flexibility.
--   * pr_tags     — N:M join. ON DELETE CASCADE on the tag side so
--                   deleting a tag cleans up every PR-tag link in one
--                   pass (and SQLite handles it without a manual sweep).
--
-- Global, not per-identity: tags are personal-to-the-user, not
-- personal-to-each-account. A workstream like "auth-migration" can
-- legitimately span PRs from multiple identities, and forcing the user
-- to recreate the tag in each profile would be friction without
-- benefit. (User confirmed this choice on 2026-05-21.)
--
-- Star/flag (column flagged_at on pull_requests) stays as-is. In
-- group-by-tag mode the inbox synthesizes a "★ Starred" group from
-- that column — no data migration, no semantic shift.

CREATE TABLE IF NOT EXISTS tags (
    name        TEXT PRIMARY KEY COLLATE NOCASE,
    color       TEXT NOT NULL,
    created_at  TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS pr_tags (
    pr_url      TEXT NOT NULL,
    tag_name    TEXT NOT NULL COLLATE NOCASE,
    added_at    TEXT NOT NULL,
    PRIMARY KEY (pr_url, tag_name),
    FOREIGN KEY (tag_name) REFERENCES tags(name) ON DELETE CASCADE ON UPDATE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_pr_tags_tag ON pr_tags(tag_name);
CREATE INDEX IF NOT EXISTS idx_pr_tags_url ON pr_tags(pr_url);
