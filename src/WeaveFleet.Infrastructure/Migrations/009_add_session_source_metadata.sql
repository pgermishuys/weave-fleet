ALTER TABLE workspaces ADD COLUMN source_provider_id TEXT;
ALTER TABLE workspaces ADD COLUMN source_type TEXT;
ALTER TABLE workspaces ADD COLUMN source_resource_id TEXT;
ALTER TABLE workspaces ADD COLUMN source_resource_url TEXT;
ALTER TABLE workspaces ADD COLUMN source_title TEXT;
ALTER TABLE workspaces ADD COLUMN source_summary TEXT;
ALTER TABLE workspaces ADD COLUMN source_resolved_at TEXT;

CREATE TABLE IF NOT EXISTS session_source_usages (
  id TEXT PRIMARY KEY,
  session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  workspace_id TEXT REFERENCES workspaces(id) ON DELETE SET NULL,
  provider_id TEXT NOT NULL,
  source_type TEXT NOT NULL,
  action_id TEXT NOT NULL,
  resource_id TEXT,
  resource_url TEXT,
  title TEXT,
  summary TEXT,
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS idx_session_source_usages_session_id ON session_source_usages(session_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_session_source_usages_workspace_id ON session_source_usages(workspace_id);
