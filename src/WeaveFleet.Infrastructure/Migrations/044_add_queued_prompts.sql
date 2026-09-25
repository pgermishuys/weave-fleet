-- Migration 044: Messages the user queued while a session's agent was working.
-- Fleet sends the first when the turn ends, then the next when that one's turn ends. The queue used to live in the
-- browser, so it was lost when the user left the session, reloaded or closed the tab, and nothing was sent.
-- kind says how an item goes out: 'prompt' (a message), 'command' (a slash command: command + arguments) or 'shell'
-- (a shell command the user runs, without a turn). position orders a session's items; the lowest goes first.

CREATE TABLE IF NOT EXISTS queued_prompts (
  id TEXT PRIMARY KEY,
  session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  position INTEGER NOT NULL,
  kind TEXT NOT NULL,
  text TEXT NOT NULL,
  command TEXT,
  arguments TEXT,
  agent TEXT,
  provider_id TEXT,
  model_id TEXT,
  effort TEXT,
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS ix_queued_prompts_session ON queued_prompts (session_id, position);
