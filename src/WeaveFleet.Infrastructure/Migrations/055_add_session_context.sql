-- Migration 055: Session context.
-- How full each session's context window is, from what its harness reports after each model call: the tokens in
-- the context (used, null before the first call and right after a compaction), the model's window (context_limit)
-- and where the harness compacts on its own (compacts_at), when known. last_call_json holds the last call's tokens
-- by kind; turns_json the size at the end of each recent turn. One row per session, replaced whenever it changes.

CREATE TABLE IF NOT EXISTS session_context (
  session_id TEXT PRIMARY KEY REFERENCES sessions(id) ON DELETE CASCADE,
  user_id TEXT NOT NULL,
  used INTEGER,
  context_limit INTEGER,
  compacts_at INTEGER,
  model_id TEXT,
  provider_id TEXT,
  last_call_json TEXT,
  last_call_at TEXT,
  compacting INTEGER NOT NULL DEFAULT 0,
  compacted_at TEXT,
  compaction_error TEXT,
  turns_json TEXT NOT NULL DEFAULT '[]',
  updated_at TEXT NOT NULL
);
