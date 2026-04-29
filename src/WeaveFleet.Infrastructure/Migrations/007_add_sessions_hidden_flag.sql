ALTER TABLE sessions ADD COLUMN is_hidden INTEGER NOT NULL DEFAULT 0;

CREATE INDEX IF NOT EXISTS idx_sessions_hidden_parent ON sessions(is_hidden, parent_session_id);
