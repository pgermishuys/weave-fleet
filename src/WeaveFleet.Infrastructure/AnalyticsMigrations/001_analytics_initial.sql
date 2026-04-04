-- Analytics schema: initial migration
-- token_events: append-only fact table, one row per assistant message with token data

CREATE TABLE token_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    event_id TEXT NOT NULL UNIQUE,
    session_id TEXT NOT NULL,
    project_id TEXT,
    project_name TEXT,
    workspace_directory TEXT,
    model_id TEXT,
    provider_id TEXT,
    tokens_input REAL NOT NULL DEFAULT 0,
    tokens_output REAL NOT NULL DEFAULT 0,
    tokens_reasoning REAL NOT NULL DEFAULT 0,
    tokens_cache_read REAL NOT NULL DEFAULT 0,
    tokens_cache_write REAL NOT NULL DEFAULT 0,
    tokens_total REAL NOT NULL DEFAULT 0,
    cost REAL NOT NULL DEFAULT 0,
    estimated_cost REAL,
    created_at TEXT NOT NULL
);

-- session_snapshots: one row per session, updated on create + stop
CREATE TABLE session_snapshots (
    session_id TEXT PRIMARY KEY,
    parent_session_id TEXT,
    project_id TEXT,
    project_name TEXT,
    workspace_directory TEXT,
    title TEXT,
    status TEXT,
    total_tokens REAL NOT NULL DEFAULT 0,
    total_cost REAL NOT NULL DEFAULT 0,
    total_estimated_cost REAL NOT NULL DEFAULT 0,
    message_count INTEGER NOT NULL DEFAULT 0,
    model_ids TEXT,
    created_at TEXT NOT NULL,
    ended_at TEXT,
    duration_seconds REAL
);

-- daily_rollups: materialized aggregates computed by AnalyticsRollupService
CREATE TABLE daily_rollups (
    date TEXT NOT NULL,
    project_id TEXT NOT NULL DEFAULT '',
    model_id TEXT NOT NULL DEFAULT '',
    provider_id TEXT NOT NULL DEFAULT '',
    total_tokens REAL NOT NULL DEFAULT 0,
    total_cost REAL NOT NULL DEFAULT 0,
    total_estimated_cost REAL NOT NULL DEFAULT 0,
    session_count INTEGER NOT NULL DEFAULT 0,
    message_count INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (date, project_id, model_id, provider_id)
);

CREATE INDEX idx_token_events_session ON token_events(session_id);
CREATE INDEX idx_token_events_created ON token_events(created_at);
CREATE INDEX idx_token_events_project ON token_events(project_id);
CREATE INDEX idx_token_events_model ON token_events(model_id);
CREATE INDEX idx_session_snapshots_project ON session_snapshots(project_id);
CREATE INDEX idx_session_snapshots_created ON session_snapshots(created_at);
CREATE INDEX idx_daily_rollups_date ON daily_rollups(date);
