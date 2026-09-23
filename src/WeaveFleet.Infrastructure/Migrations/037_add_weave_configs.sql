-- Migration 037: The Weave config a user keeps in Fleet.
-- weave_configs.source is 'own' (Weave reads the user's own files; Fleet sets nothing) or 'fleet' (Fleet writes the
-- files below into a folder of its own and points Weave at it). No row means 'own'.
-- weave_config_files holds the files by their path in that folder: config.weave, prompts/<name>.md,
-- weave-opencode.jsonc.
CREATE TABLE IF NOT EXISTS weave_configs (
    user_id TEXT PRIMARY KEY,
    source TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS weave_config_files (
    user_id TEXT NOT NULL,
    path TEXT NOT NULL,
    content TEXT NOT NULL,
    PRIMARY KEY (user_id, path)
);
