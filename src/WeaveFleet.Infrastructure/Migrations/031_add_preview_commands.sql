-- Migration 031: Preview commands.
-- The last command that served a page in a project, so the browser canvas's + menu can offer it again in
-- the next session. A project is the folder a session's worktree came from, or the session's folder when it
-- works in place, so every worktree of one repository shares it. Not tied to a session: it outlives them.

CREATE TABLE IF NOT EXISTS preview_commands (
  user_id TEXT NOT NULL,
  project_directory TEXT NOT NULL,
  command TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  PRIMARY KEY (user_id, project_directory)
);
