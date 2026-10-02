-- Migration 047: What agents did in their own browser tabs, step by step.
-- Fleet runs every browser action an agent takes (OpenCode 2's browser plugin, OpenCode's fleet_browser_* tools), so it
-- keeps each one as a step: the conversation lists them under the tool call that made them, and they're still there
-- after a reload. step_json is the step in Fleet's BrowserStep shape; seq orders a session's steps from 1.

CREATE TABLE IF NOT EXISTS agent_browser_steps (
  session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  seq INTEGER NOT NULL,
  at TEXT NOT NULL,
  step_json TEXT NOT NULL,
  PRIMARY KEY (session_id, seq)
);
