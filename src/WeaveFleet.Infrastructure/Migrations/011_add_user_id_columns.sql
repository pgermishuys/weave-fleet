-- Migration 011: Add user_id columns for multi-tenancy
-- Existing rows receive user_id = 'local-user' (backwards-compatible default).

ALTER TABLE projects          ADD COLUMN user_id TEXT NOT NULL DEFAULT 'local-user';
ALTER TABLE sessions          ADD COLUMN user_id TEXT NOT NULL DEFAULT 'local-user';
ALTER TABLE workspaces        ADD COLUMN user_id TEXT NOT NULL DEFAULT 'local-user';
ALTER TABLE instances         ADD COLUMN user_id TEXT NOT NULL DEFAULT 'local-user';
ALTER TABLE workspace_roots   ADD COLUMN user_id TEXT NOT NULL DEFAULT 'local-user';

-- Indexes for per-user list queries
CREATE INDEX IF NOT EXISTS idx_projects_user_id  ON projects(user_id);
CREATE INDEX IF NOT EXISTS idx_sessions_user_id  ON sessions(user_id);
CREATE INDEX IF NOT EXISTS idx_workspaces_user_id ON workspaces(user_id);
CREATE INDEX IF NOT EXISTS idx_instances_user_id ON instances(user_id);
CREATE INDEX IF NOT EXISTS idx_workspace_roots_user_id ON workspace_roots(user_id);

-- Compound indexes for the most common filtered queries
CREATE INDEX IF NOT EXISTS idx_sessions_user_status  ON sessions(user_id, status);
CREATE INDEX IF NOT EXISTS idx_projects_user_type    ON projects(user_id, type);
