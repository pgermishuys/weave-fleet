-- Migration 062: The skills a queued message named.
-- A message can name skills with $ in its text ($fleet-debug). The queue keeps them beside the text, so when the message
-- is sent after the turn ends it names the same skills as when it was typed. Names are space-separated: a skill's name
-- can't hold a space.

ALTER TABLE queued_prompts ADD COLUMN skills TEXT;
