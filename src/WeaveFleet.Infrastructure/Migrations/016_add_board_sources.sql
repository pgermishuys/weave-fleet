-- Migration 016: Add board source table for external board sync configuration.
-- Uses TEXT timestamps and snake_case naming to match existing SQLite conventions.

CREATE TABLE board_sources (
    id             TEXT NOT NULL PRIMARY KEY,
    board_id       TEXT NOT NULL REFERENCES boards(id) ON DELETE CASCADE,
    provider_type  TEXT NOT NULL,
    config         TEXT NOT NULL,
    last_sync_at   TEXT,
    created_at     TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at     TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS idx_board_sources_board_id
    ON board_sources(board_id);
