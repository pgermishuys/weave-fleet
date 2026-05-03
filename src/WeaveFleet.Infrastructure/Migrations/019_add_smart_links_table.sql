-- Migration 019: Smart links table.
-- Stores URLs detected in session messages, enriched with live status from external providers.
-- Unique constraint on (session_id, url, user_id) prevents duplicate entries per session.

CREATE TABLE IF NOT EXISTS smart_links (
    id            TEXT    NOT NULL PRIMARY KEY,
    session_id    TEXT    NOT NULL,
    url           TEXT    NOT NULL,
    provider_id   TEXT    NOT NULL,
    resource_type TEXT    NOT NULL DEFAULT '',
    resource_id   TEXT    NOT NULL DEFAULT '',
    title         TEXT    NOT NULL DEFAULT '',
    status        TEXT    NOT NULL DEFAULT '',
    status_label  TEXT    NOT NULL DEFAULT '',
    metadata_json TEXT,
    is_dismissed  INTEGER NOT NULL DEFAULT 0,
    is_terminal   INTEGER NOT NULL DEFAULT 0,
    created_at    TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    updated_at    TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    user_id       TEXT    NOT NULL,
    UNIQUE (session_id, url, user_id)
);

CREATE INDEX IF NOT EXISTS idx_smart_links_session_user
    ON smart_links (session_id, user_id);
