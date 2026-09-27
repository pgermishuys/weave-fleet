-- Migration 046: The failure a turn stopped with, on a message Fleet keeps for it.
-- A turn that fails before the model answers (an unknown model, a provider that refuses) leaves nothing in the
-- harness's history, so after a reload the conversation showed the prompt with no answer and no reason. Fleet saves
-- such a failure as an assistant message with no parts; error_json is the harness's error in Fleet's TurnError shape.

ALTER TABLE messages ADD COLUMN error_json TEXT;
