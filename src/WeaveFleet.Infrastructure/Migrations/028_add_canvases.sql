-- Migration 028: Canvases.
-- Per-session canvases that the user and the agent both change. Every accepted change bumps
-- canvases.version and records the ops that produced it in canvas_revisions.
-- agent_seen_version is the last version the agent read or wrote.

CREATE TABLE IF NOT EXISTS canvases (
  id TEXT PRIMARY KEY,
  session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  user_id TEXT NOT NULL,
  kind TEXT NOT NULL,
  title TEXT NOT NULL,
  state_json TEXT NOT NULL,
  version INTEGER NOT NULL,
  agent_seen_version INTEGER NOT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  closed_at TEXT
);

CREATE INDEX IF NOT EXISTS ix_canvases_session ON canvases(session_id);

CREATE TABLE IF NOT EXISTS canvas_revisions (
  canvas_id TEXT NOT NULL REFERENCES canvases(id) ON DELETE CASCADE,
  version INTEGER NOT NULL,
  actor TEXT NOT NULL,
  ops_json TEXT NOT NULL,
  created_at TEXT NOT NULL,
  PRIMARY KEY (canvas_id, version)
);
