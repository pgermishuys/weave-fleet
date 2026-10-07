-- Migration 057: Turns Fleet tries again by itself.
-- When a model provider's rate limit, usage limit or overload stops a session's turn, Fleet sends "Continue where you
-- left off." at due_at: when the limit resets, if the provider said, or after a wait that grows with each attempt.
-- One row per session. state is 'waiting' until it's sent, then 'sent' until a turn ends without a limit stopping
-- it, so the next failure counts as the next attempt. provider_said is 1 when due_at came from the provider.

CREATE TABLE IF NOT EXISTS scheduled_retries (
  session_id TEXT PRIMARY KEY REFERENCES sessions(id) ON DELETE CASCADE,
  user_id TEXT NOT NULL,
  due_at TEXT NOT NULL,
  attempt INTEGER NOT NULL,
  kind TEXT NOT NULL,
  reason TEXT NOT NULL,
  provider_said INTEGER NOT NULL DEFAULT 0,
  state TEXT NOT NULL DEFAULT 'waiting',
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS ix_scheduled_retries_state ON scheduled_retries (state, due_at);
