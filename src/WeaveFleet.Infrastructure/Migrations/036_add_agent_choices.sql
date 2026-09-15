-- Migration 036: The agent a session was given, and the harness an automation's agent and model belong to.
-- sessions.selected_agent is the agent the session was started with or last prompted with by name; a prompt that
-- names no agent goes to it, the way selected_provider_id/selected_model_id (migration 018) work for the model.
-- NULL means the harness's default agent.
-- automations.harness_type is the harness an automation's runs use. It's set when an automation names an agent or
-- a model, which only mean something on the harness they came from. NULL means the default harness at run time,
-- which is how every automation made before this runs.

ALTER TABLE sessions ADD COLUMN selected_agent TEXT;
ALTER TABLE automations ADD COLUMN harness_type TEXT;
