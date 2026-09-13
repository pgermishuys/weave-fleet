-- Migration 030: App runs.
-- Commands Fleet runs for a session, usually dev servers shown in a browser canvas. A run keeps its id
-- and port across restarts, so the canvas that shows it keeps working. Output stays in memory.
-- pid and pid_started_at name the process Fleet last started, so Fleet can kill it at startup if it
-- outlived a crash. Fleet never starts a stored run on its own.

CREATE TABLE IF NOT EXISTS app_runs (
  id TEXT PRIMARY KEY,
  session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  user_id TEXT NOT NULL,
  command TEXT NOT NULL,
  directory TEXT NOT NULL,
  port INTEGER NOT NULL,
  status TEXT NOT NULL,
  exit_code INTEGER,
  url TEXT,
  pid INTEGER,
  pid_started_at TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_app_runs_session ON app_runs(session_id);
