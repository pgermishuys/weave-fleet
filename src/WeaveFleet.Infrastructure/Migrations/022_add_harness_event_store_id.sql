-- Migration 022: Store the canonical durable event id on harness_events.
-- The pre-existing sequence_number column is the relay pump sequence and can
-- reset across harness restarts. event_id is the inproc_events.id value and is
-- the cursor used by committed-events gap-fill.

ALTER TABLE harness_events ADD COLUMN event_id INTEGER;

UPDATE harness_events
SET event_id = id
WHERE event_id IS NULL;

CREATE INDEX IF NOT EXISTS idx_harness_events_session_event_id
    ON harness_events(session_id, event_id);

CREATE UNIQUE INDEX IF NOT EXISTS idx_harness_events_event_id
    ON harness_events(event_id);

DROP INDEX IF EXISTS idx_harness_events_session_sequence;

CREATE INDEX IF NOT EXISTS idx_harness_events_session_sequence
    ON harness_events(session_id, sequence_number);
