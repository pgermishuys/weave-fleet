-- Migration 042: The slash command a user message came from.
-- A harness turns a command into a prompt of its own (OpenCode puts the command's whole template in the user message),
-- so the conversation would show that prompt instead of what the user sent. Fleet keeps "/name arguments" here, by the
-- id the harness stored the message under, and the conversation shows the command.
-- message_id is the harness's id, which isn't always Fleet's (OpenCode 2 picks its own), so there is no foreign key to
-- messages: a harness that keeps its own history has no row there.

CREATE TABLE IF NOT EXISTS message_commands (
  session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  message_id TEXT NOT NULL,
  command TEXT NOT NULL,
  arguments TEXT,
  created_at TEXT NOT NULL DEFAULT (datetime('now')),
  PRIMARY KEY (session_id, message_id)
);
