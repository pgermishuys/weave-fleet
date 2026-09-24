-- Migration 041: Automations that run a workflow.
-- An automation whose target_type is 'workflow' starts a workflow run instead of a session. workflow_id is the workflow
-- (builtin:… or repo:…) in the repository workspace_id names; workflow_steps is a JSON array of the optional steps
-- switched on for its runs. Both are NULL for every other target.
-- automation_runs.workflow_run_id is the workflow run an automation run started; the run's state follows it.
-- workflow_runs.automation_id and automation_name say which automation started a run ("Started by …"); the name is kept
-- as it was then, so the run still reads right after the automation is renamed or deleted. NULL for Run box runs.

ALTER TABLE automations ADD COLUMN workflow_id TEXT;
ALTER TABLE automations ADD COLUMN workflow_steps TEXT;
ALTER TABLE automation_runs ADD COLUMN workflow_run_id TEXT;
ALTER TABLE workflow_runs ADD COLUMN automation_id TEXT;
ALTER TABLE workflow_runs ADD COLUMN automation_name TEXT;
