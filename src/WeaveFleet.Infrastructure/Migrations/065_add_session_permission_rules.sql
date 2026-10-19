-- Migration 065: "Don't ask again" rules, kept per session.
-- One row per click on "Don't ask again" in a permission card. patterns is a JSON array of strings (one click can
-- allow several). Fleet's PermissionGate loads them when a session's harness is attached, so they outlast a restart,
-- and the Allowed list in the composer shows and removes them.

CREATE TABLE IF NOT EXISTS session_permission_rules (
  id TEXT PRIMARY KEY,
  session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  kind TEXT NOT NULL,
  tool TEXT NOT NULL,
  patterns TEXT NOT NULL,
  created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_session_permission_rules_session ON session_permission_rules(session_id, created_at);
