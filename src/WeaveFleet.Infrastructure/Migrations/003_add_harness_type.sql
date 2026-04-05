ALTER TABLE sessions ADD COLUMN harness_type TEXT NOT NULL DEFAULT 'opencode';
CREATE INDEX idx_sessions_harness_type ON sessions(harness_type);
