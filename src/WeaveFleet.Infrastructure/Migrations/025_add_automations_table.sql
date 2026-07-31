CREATE TABLE IF NOT EXISTS automations (
  id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  prompt TEXT NOT NULL,
  trigger_type TEXT NOT NULL,
  trigger_config TEXT NOT NULL,
  max_concurrent_runs INTEGER NOT NULL DEFAULT 1,
  max_runs_per_hour INTEGER NOT NULL DEFAULT 10,
  timeout_minutes INTEGER NOT NULL DEFAULT 30,
  is_enabled INTEGER NOT NULL DEFAULT 0,
  is_deleted INTEGER NOT NULL DEFAULT 0,
  workspace_id TEXT,
  model TEXT,
  agent TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT,
  user_id TEXT NOT NULL
);
