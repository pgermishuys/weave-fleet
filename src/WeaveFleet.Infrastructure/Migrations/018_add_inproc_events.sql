-- Migration 018: Durable event log for the in-process event bus.
-- Stores every durable harness event (those classified as IsDurable by EventTypeMetadata)
-- so they survive server restarts and can be replayed to projections on startup.
--
-- message_id is "{sessionId}:{sequence}" (same scheme as NATS Msg-Id dedup).
-- UNIQUE constraint makes AppendAsync idempotent on retry.
-- dispatched_at IS NULL means the event has not yet been delivered to all projections.

CREATE TABLE IF NOT EXISTS inproc_events (
    id            INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    message_id    TEXT    NOT NULL UNIQUE,
    session_id    TEXT    NOT NULL,
    project_id    TEXT    NOT NULL,
    tenant        TEXT    NOT NULL,
    event_type    TEXT    NOT NULL,
    payload       TEXT    NOT NULL,
    user_id       TEXT,
    harness_type  TEXT,
    sequence      INTEGER NOT NULL,
    created_at    TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    dispatched_at TEXT
);

CREATE INDEX IF NOT EXISTS idx_inproc_events_undispatched
    ON inproc_events (id)
    WHERE dispatched_at IS NULL;
