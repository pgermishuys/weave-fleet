-- Analytics migration 003: add user_id to all analytics tables for tenant isolation

-- Add user_id to token_events. Legacy rows are intentionally backfilled to '' so
-- pre-migration data is hidden rather than assigned to a synthetic shared owner.
ALTER TABLE token_events ADD COLUMN user_id TEXT NOT NULL DEFAULT '';

-- Add user_id to session_snapshots. Legacy rows are intentionally backfilled to ''.
ALTER TABLE session_snapshots ADD COLUMN user_id TEXT NOT NULL DEFAULT '';

-- Recreate daily_rollups with user_id in composite PK
-- SQLite does not support ALTER TABLE ... ADD PRIMARY KEY, so we must recreate the table.
CREATE TABLE daily_rollups_new (
    date TEXT NOT NULL,
    user_id TEXT NOT NULL DEFAULT '',
    project_id TEXT NOT NULL DEFAULT '',
    model_id TEXT NOT NULL DEFAULT '',
    provider_id TEXT NOT NULL DEFAULT '',
    total_tokens REAL NOT NULL DEFAULT 0,
    total_cost REAL NOT NULL DEFAULT 0,
    total_estimated_cost REAL NOT NULL DEFAULT 0,
    session_count INTEGER NOT NULL DEFAULT 0,
    message_count INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (date, user_id, project_id, model_id, provider_id)
);

INSERT INTO daily_rollups_new (
    date, user_id, project_id, model_id, provider_id,
    total_tokens, total_cost, total_estimated_cost,
    session_count, message_count
)
SELECT
    date, '' AS user_id, project_id, model_id, provider_id,
    total_tokens, total_cost, total_estimated_cost,
    session_count, message_count
FROM daily_rollups;

DROP TABLE daily_rollups;
ALTER TABLE daily_rollups_new RENAME TO daily_rollups;

-- Recreate indexes
CREATE INDEX idx_daily_rollups_date ON daily_rollups(date);

-- New user-scoped indexes
CREATE INDEX idx_token_events_user ON token_events(user_id);
CREATE INDEX idx_session_snapshots_user ON session_snapshots(user_id);
