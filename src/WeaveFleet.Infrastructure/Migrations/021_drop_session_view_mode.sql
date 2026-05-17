-- Migration 021: Drop view_mode column from the sessions table.
-- view_mode was used to distinguish sessions-v1 (workspace-centric) from
-- sessions-v2 (project-grouped). Sessions-v1 has been removed; only v2 remains.

DROP INDEX IF EXISTS idx_sessions_view_mode_user;
ALTER TABLE sessions DROP COLUMN view_mode;
