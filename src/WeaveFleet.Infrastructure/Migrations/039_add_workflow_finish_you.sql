-- Migration 039: Workflow steps you finish together with the agent.
-- workflow_run_steps.finish is who ends the visit: 'agent' (it calls fleet_step_done) or 'you' (you press Move on).
-- It's decided when the visit starts, from the step's finish: you or the run's Check with me, and never changes.
-- NULL on older rows means 'agent'. prompt_message_id is the id of the prompt the step's session started with;
-- wrap_up_message_id the one Fleet sent when you pressed Move on, whose reply becomes the step's summary.
-- hand_off_note is the note you wrote for the next step. files_checked is 1 once the step's declared files were
-- found before the next step started.
-- workflow_runs.waiting_kind says what a waiting run waits on ('missing-files', 'wrap-up-failed', ...); NULL on
-- older rows, which the view works out from the visit as before.
-- sessions.workflow_user_finishes is 1 for a step session the user finishes: it has the step tool hidden like any
-- session that isn't a step.

ALTER TABLE workflow_run_steps ADD COLUMN finish TEXT;
ALTER TABLE workflow_run_steps ADD COLUMN prompt_message_id TEXT;
ALTER TABLE workflow_run_steps ADD COLUMN wrap_up_message_id TEXT;
ALTER TABLE workflow_run_steps ADD COLUMN hand_off_note TEXT;
ALTER TABLE workflow_run_steps ADD COLUMN files_checked INTEGER NOT NULL DEFAULT 0;

ALTER TABLE workflow_runs ADD COLUMN waiting_kind TEXT;

ALTER TABLE sessions ADD COLUMN workflow_user_finishes INTEGER NOT NULL DEFAULT 0;
