-- Migration 013: Add transactional outbox storage for committed event delivery.
-- SQLite stores JSON payloads as TEXT rather than jsonb/Postgres-native JSON types.

CREATE TABLE IF NOT EXISTS outbox_messages (
    id              INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    topic           TEXT NOT NULL,
    type            TEXT NOT NULL,
    payload         TEXT NOT NULL,
    user_id         TEXT,
    created_at      TEXT NOT NULL DEFAULT (datetime('now')),
    available_at    TEXT NOT NULL DEFAULT (datetime('now')),
    dispatched_at   TEXT
);

CREATE INDEX IF NOT EXISTS idx_outbox_messages_dispatched_at_id
    ON outbox_messages(dispatched_at, id);

CREATE INDEX IF NOT EXISTS idx_outbox_messages_topic_id
    ON outbox_messages(topic, id);
