-- Migration 061: Which mod put a prompt in the conversation.
-- A mod can queue a follow-up (queued_prompts.mod holds its name, so the queue row says who) and rewrite the words the
-- user typed. Either way the prompt would pass for the user's own, so Fleet keeps a mark by the message id the harness
-- stored it under: kind is 'sent' (a mod queued it) or 'rewritten' (a mod changed the typed words), mod is the mod's name.
-- message_id is the harness's id, which isn't always Fleet's, so there is no foreign key to messages (as message_commands).

ALTER TABLE queued_prompts ADD COLUMN mod TEXT;

CREATE TABLE IF NOT EXISTS message_mods (
  session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  message_id TEXT NOT NULL,
  kind TEXT NOT NULL,
  mod TEXT NOT NULL,
  PRIMARY KEY (session_id, message_id)
);
