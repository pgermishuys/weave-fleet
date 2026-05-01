-- Migration 017: Per-session harness event log used as the source for the
-- /api/sessions/{id}/committed-events gap-fill API. Populated by
-- MessagePersistenceProjection after the unified-fan-out refactor moved
-- harness events off the outbox dispatcher path.
--
-- sequence_number is HarnessEventRelay's per-pump monotonic counter
-- (set in publish headers as x-fleet-sequence). Unique per (session_id,
-- sequence_number) so the projection upsert is idempotent on JetStream
-- redelivery.

CREATE TABLE IF NOT EXISTS harness_events (
    id              INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    session_id      TEXT NOT NULL,
    sequence_number INTEGER NOT NULL,
    type            TEXT NOT NULL,
    payload         TEXT NOT NULL,
    user_id         TEXT,
    created_at      TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_harness_events_session_sequence
    ON harness_events(session_id, sequence_number);
