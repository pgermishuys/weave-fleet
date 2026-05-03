-- Durable user preferences (key-value, scoped per user).
-- Used for settings like default harness type, workspace display name, etc.
CREATE TABLE IF NOT EXISTS user_preferences (
    user_id    TEXT    NOT NULL,
    key        TEXT    NOT NULL,
    value      TEXT    NOT NULL,
    updated_at TEXT    NOT NULL DEFAULT (datetime('now')),
    PRIMARY KEY (user_id, key)
);
