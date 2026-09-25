-- Migration 043: Side conversations (/btw in the composer).
-- A side conversation is a fork of a session, run as a hidden session of its own and shown only in that session's
-- conversation panel. sessions.side_of_session_id is the session it belongs to; lists leave such sessions out.
-- side_boundary_message_id is the newest message the fork copied, so the panel shows only what came after it.
-- kept_from_side is 1 for a side conversation the user kept as a session of its own: its prompts carry a note that
-- lifts the side conversation's rules, which stay in its history.

ALTER TABLE sessions ADD COLUMN side_of_session_id TEXT;
ALTER TABLE sessions ADD COLUMN side_boundary_message_id TEXT;
ALTER TABLE sessions ADD COLUMN kept_from_side INTEGER NOT NULL DEFAULT 0;

CREATE INDEX IF NOT EXISTS idx_sessions_side_of_session_id ON sessions(side_of_session_id);
