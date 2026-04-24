-- Migration 015: Add board, lane, and card tables for kanban board persistence.
-- Uses TEXT timestamps and snake_case naming to match existing SQLite conventions.

CREATE TABLE boards (
    id          TEXT NOT NULL PRIMARY KEY,
    user_id     TEXT NOT NULL,
    name        TEXT NOT NULL DEFAULT 'My Board',
    created_at  TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at  TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE board_lanes (
    id          TEXT NOT NULL PRIMARY KEY,
    board_id    TEXT NOT NULL REFERENCES boards(id) ON DELETE CASCADE,
    name        TEXT NOT NULL,
    position    INTEGER NOT NULL,
    is_inbox    INTEGER NOT NULL DEFAULT 0,
    created_at  TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at  TEXT NOT NULL DEFAULT (datetime('now')),
    CHECK (is_inbox IN (0, 1))
);

CREATE TABLE board_cards (
    id           TEXT NOT NULL PRIMARY KEY,
    board_id     TEXT NOT NULL REFERENCES boards(id) ON DELETE CASCADE,
    lane_id      TEXT NOT NULL REFERENCES board_lanes(id),
    title        TEXT NOT NULL,
    source_type  TEXT,
    source_key   TEXT,
    metadata     TEXT,
    position     INTEGER NOT NULL,
    archived_at  TEXT,
    created_at   TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at   TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS idx_boards_user_id
    ON boards(user_id);

CREATE INDEX IF NOT EXISTS idx_board_lanes_board_id_position
    ON board_lanes(board_id, position);

CREATE INDEX IF NOT EXISTS idx_board_cards_board_id
    ON board_cards(board_id);

CREATE INDEX IF NOT EXISTS idx_board_cards_lane_id_position
    ON board_cards(lane_id, position);

CREATE INDEX IF NOT EXISTS idx_board_cards_source_type_source_key
    ON board_cards(source_type, source_key);
