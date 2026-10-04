-- Migration 049: Telling the agent about work it lost.
-- Work that ended with its harness or Fleet (ended_reason = 'lost') never reports back, so the session's next prompt
-- carries a one-time note naming it. lost_reported_at is when that note went to the agent; until then the work is still
-- to be told. Work lost before this migration is left out: it's from before the note existed.

ALTER TABLE delegations ADD COLUMN lost_reported_at TEXT;

UPDATE delegations SET lost_reported_at = COALESCE(completed_at, updated_at) WHERE ended_reason = 'lost';

-- Only the few rows still to be told, so looking for them costs a session with none nothing.
CREATE INDEX IF NOT EXISTS idx_delegations_lost_unreported ON delegations(parent_session_id)
    WHERE ended_reason = 'lost' AND lost_reported_at IS NULL;
