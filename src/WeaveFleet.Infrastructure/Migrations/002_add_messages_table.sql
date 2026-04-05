CREATE TABLE messages (
  id TEXT NOT NULL,
  session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  role TEXT NOT NULL,
  parts_json TEXT NOT NULL,
  timestamp TEXT NOT NULL,
  created_at TEXT NOT NULL DEFAULT (datetime('now')),
  PRIMARY KEY (id, session_id)
);

-- Cursor-based pagination: fetch messages for a session ordered by timestamp descending
CREATE INDEX idx_messages_session_timestamp ON messages(session_id, timestamp DESC);
