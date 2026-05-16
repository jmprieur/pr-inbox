-- Migration 007 — Dossier version tracking.
--
-- Adds a per-PR integer column so the enrich pass can identify rows that were
-- enriched before a dossier upgrade (e.g. migration 006 added body/files/
-- mergeable_state/ci_status/thread-body but existing snapshots are dedup-
-- protected from re-enrichment until canonical state changes).
--
-- Semantics:
--   * 0  → never enriched against the current dossier schema. Eligible for
--          a one-shot backfill regardless of enrich_state.
--   * >0 → matches BriefService.CurrentDossierVersion at the time of enrich.
--          The column is bumped when the schema/render contract grows so the
--          backfill pass picks up legacy rows automatically.
--
-- MigrationRunner gates re-runs via the schema_version table, so this script
-- runs exactly once per DB.

ALTER TABLE pull_requests ADD COLUMN dossier_version INTEGER NOT NULL DEFAULT 0;
