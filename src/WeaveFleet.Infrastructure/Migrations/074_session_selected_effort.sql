-- Migration 074: The effort a session was last prompted with.
-- sessions.selected_effort is the effort level the last prompt that named one was sent with (or the reserved
-- 'fleet:default'), the way selected_agent and selected_model_id work. A prompt Fleet sends without the composer
-- (Continue after a restart, an automation) goes with it instead of the harness default. NULL means none was named.

ALTER TABLE sessions ADD COLUMN selected_effort TEXT;
