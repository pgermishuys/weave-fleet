-- Migration 020: Add view_mode column to sessions table.
-- Discriminates between V1 (workspace-grouped) and V2 (project-grouped) session views.
-- All existing sessions default to 'v2'.

ALTER TABLE sessions ADD COLUMN view_mode TEXT NOT NULL DEFAULT 'v2';

CREATE INDEX IF NOT EXISTS idx_sessions_view_mode_user
    ON sessions (view_mode, user_id);
