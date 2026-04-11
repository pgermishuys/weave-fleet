-- Migration 012: Add user_credentials table for encrypted per-user credential storage.
-- Credentials are identified by namespace/kind (not env var names).
-- Multiple credentials per user per namespace/kind are supported.
-- Unique constraint on (user_id, label) prevents ambiguous display names.

CREATE TABLE user_credentials (
    id              TEXT NOT NULL PRIMARY KEY,
    user_id         TEXT NOT NULL,
    namespace       TEXT NOT NULL,
    kind            TEXT NOT NULL,
    label           TEXT NOT NULL,
    encrypted_value TEXT NOT NULL,
    display_hint    TEXT NOT NULL DEFAULT '',
    metadata        TEXT,
    created_at      TEXT NOT NULL,
    updated_at      TEXT NOT NULL,
    UNIQUE (user_id, label)
);

-- Index for per-user list queries
CREATE INDEX IF NOT EXISTS idx_user_credentials_user_id
    ON user_credentials(user_id);

-- Compound index for namespace/kind lookups (used by harness credential selection)
CREATE INDEX IF NOT EXISTS idx_user_credentials_user_namespace_kind
    ON user_credentials(user_id, namespace, kind);
