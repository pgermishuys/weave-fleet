CREATE TABLE delegations (
  id TEXT PRIMARY KEY,
  parent_session_id TEXT NOT NULL REFERENCES sessions(id),
  child_session_id TEXT REFERENCES sessions(id),
  parent_tool_call_id TEXT,
  title TEXT NOT NULL,
  status TEXT NOT NULL,
  created_at TEXT NOT NULL DEFAULT (datetime('now')),
  updated_at TEXT NOT NULL DEFAULT (datetime('now')),
  completed_at TEXT
);

CREATE INDEX idx_delegations_parent_session_id ON delegations(parent_session_id);
CREATE INDEX idx_delegations_child_session_id ON delegations(child_session_id);
CREATE INDEX idx_delegations_parent_tool_call_id ON delegations(parent_tool_call_id);
