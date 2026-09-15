-- Migration 035: Harness profiles, and the one a session started with.
-- A profile is harness config the user picks per session (for OpenCode, an opencode.json layered over their own).
-- sessions.harness_profile_id NULL means no profile: the harness's own config as it is.
CREATE TABLE IF NOT EXISTS harness_profiles (
    id TEXT PRIMARY KEY,
    user_id TEXT NOT NULL,
    harness_type TEXT NOT NULL,
    name TEXT NOT NULL,
    content TEXT NOT NULL,
    is_default INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_harness_profiles_user_harness ON harness_profiles(user_id, harness_type);

ALTER TABLE sessions ADD COLUMN harness_profile_id TEXT;
