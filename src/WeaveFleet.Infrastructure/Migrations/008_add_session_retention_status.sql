ALTER TABLE sessions ADD COLUMN retention_status TEXT NOT NULL DEFAULT 'active';
ALTER TABLE sessions ADD COLUMN archived_at TEXT;

-- Backfill existing installs to the explicit default without inferring archive
-- state from stopped/completed lifecycle fields.
UPDATE sessions
SET retention_status = 'active'
WHERE retention_status IS NULL OR retention_status = '';

CREATE INDEX idx_sessions_retention_status ON sessions(retention_status);
CREATE INDEX idx_sessions_retention_created_at ON sessions(retention_status, created_at DESC);
CREATE INDEX idx_sessions_workspace_retention_created_at ON sessions(workspace_id, retention_status, created_at DESC);
