-- Migration 032: Session progress.
-- How far along each session is, worked out from its events (the agent's todo list today). One row per
-- session, replaced whenever it changes. done/total/current are what the session list shows; detail_json
-- holds the rest (the todo items), so the open session can show it without asking the harness again.

CREATE TABLE IF NOT EXISTS session_progress (
  session_id TEXT PRIMARY KEY REFERENCES sessions(id) ON DELETE CASCADE,
  user_id TEXT NOT NULL,
  kind TEXT NOT NULL,
  done INTEGER NOT NULL,
  total INTEGER NOT NULL,
  current TEXT,
  detail_json TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
