-- Migration 063: Sessions a restart cut off.
-- turn_started_at is set when a session's turn starts and cleared when it ends (idle, failed, aborted), so a Fleet that
-- starts again can tell which sessions were working when the last one stopped. turn_waiting is 1 while the turn waits
-- on a question or permission. At startup each flagged session is checked against its history; those still cut off get
-- a row in session_interruptions, which the session's row and conversation show until Continue or Dismiss.

ALTER TABLE sessions ADD COLUMN turn_started_at TEXT;
ALTER TABLE sessions ADD COLUMN turn_waiting INTEGER NOT NULL DEFAULT 0;

CREATE TABLE IF NOT EXISTS session_interruptions (
  session_id TEXT PRIMARY KEY REFERENCES sessions(id) ON DELETE CASCADE,
  user_id TEXT NOT NULL,
  at TEXT NOT NULL,
  waiting INTEGER NOT NULL DEFAULT 0,
  last_call TEXT,
  changed_files INTEGER
);
