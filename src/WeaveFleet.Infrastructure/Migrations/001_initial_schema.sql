-- Projects table (new)
CREATE TABLE projects (
  id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  description TEXT,
  type TEXT NOT NULL DEFAULT 'user',
  position INTEGER NOT NULL DEFAULT 0,
  created_at TEXT NOT NULL DEFAULT (datetime('now')),
  updated_at TEXT NOT NULL DEFAULT (datetime('now'))
);

-- Workspaces table
CREATE TABLE workspaces (
  id TEXT PRIMARY KEY,
  directory TEXT NOT NULL,
  source_directory TEXT,
  isolation_strategy TEXT NOT NULL DEFAULT 'existing',
  branch TEXT,
  created_at TEXT NOT NULL DEFAULT (datetime('now')),
  cleaned_up_at TEXT,
  display_name TEXT
);

-- Instances table
CREATE TABLE instances (
  id TEXT PRIMARY KEY,
  port INTEGER NOT NULL,
  pid INTEGER,
  directory TEXT NOT NULL,
  url TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'running',
  created_at TEXT NOT NULL DEFAULT (datetime('now')),
  stopped_at TEXT
);

-- Sessions table (with project_id FK)
CREATE TABLE sessions (
  id TEXT PRIMARY KEY,
  workspace_id TEXT NOT NULL REFERENCES workspaces(id),
  instance_id TEXT NOT NULL REFERENCES instances(id),
  project_id TEXT REFERENCES projects(id),
  opencode_session_id TEXT NOT NULL,
  title TEXT NOT NULL DEFAULT 'Untitled',
  status TEXT NOT NULL DEFAULT 'active',
  directory TEXT NOT NULL,
  created_at TEXT NOT NULL DEFAULT (datetime('now')),
  stopped_at TEXT,
  parent_session_id TEXT,
  activity_status TEXT,
  lifecycle_status TEXT,
  total_tokens INTEGER NOT NULL DEFAULT 0,
  total_cost REAL NOT NULL DEFAULT 0
);

-- Session callbacks table
CREATE TABLE session_callbacks (
  id TEXT PRIMARY KEY,
  source_session_id TEXT NOT NULL,
  target_session_id TEXT NOT NULL,
  target_instance_id TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'pending',
  created_at TEXT NOT NULL DEFAULT (datetime('now')),
  fired_at TEXT
);

-- Workspace roots table
CREATE TABLE workspace_roots (
  id TEXT PRIMARY KEY,
  path TEXT NOT NULL UNIQUE,
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

-- Indexes
CREATE INDEX idx_callbacks_source ON session_callbacks(source_session_id, status);
CREATE INDEX idx_sessions_status ON sessions(status);
CREATE INDEX idx_sessions_parent ON sessions(parent_session_id);
CREATE INDEX idx_sessions_created_at ON sessions(created_at DESC);
CREATE INDEX idx_sessions_project ON sessions(project_id);
CREATE INDEX idx_projects_type ON projects(type);
CREATE INDEX idx_projects_position ON projects(position);
