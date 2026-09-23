-- Migration 038: Workflow runs. Fleet runs a workflow's steps itself, one session per agent step, so a run is rows:
-- a restart picks it up at its current step.
-- workflow_runs.definition is the workflow file as it was when the run started, so editing the file doesn't change a
-- run in flight. options is JSON: the optional steps switched on for the run, its model overrides, and the model
-- each step resolved to. status is 'running', 'waiting' (on the user), 'done', 'ended' (the user ended it) or
-- 'failed' (Fleet couldn't start a step).
-- workflow_run_steps keeps one row per visit to a step: a loop back to a step is a new visit. status is 'running',
-- 'waiting' (stopped without an outcome, or past a loop's maximum), 'done', 'skipped' or 'decided' (a You step).
-- sessions.workflow_run_id is the run a step session belongs to; NULL for every other session.

CREATE TABLE IF NOT EXISTS workflow_runs (
    id TEXT PRIMARY KEY,
    user_id TEXT NOT NULL,
    workflow_id TEXT NOT NULL,
    workflow_name TEXT NOT NULL,
    definition TEXT NOT NULL,
    request TEXT NOT NULL,
    slug TEXT NOT NULL,
    title TEXT NOT NULL,
    repository_path TEXT NOT NULL,
    base_branch TEXT,
    branch TEXT,
    worktree_path TEXT,
    harness_type TEXT NOT NULL,
    harness_profile_id TEXT,
    options TEXT NOT NULL,
    status TEXT NOT NULL,
    current_step_id TEXT,
    waiting_reason TEXT,
    result TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    ended_at TEXT
);

CREATE INDEX IF NOT EXISTS ix_workflow_runs_user ON workflow_runs(user_id, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_workflow_runs_status ON workflow_runs(status);

CREATE TABLE IF NOT EXISTS workflow_run_steps (
    id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL,
    step_id TEXT NOT NULL,
    visit INTEGER NOT NULL,
    session_id TEXT,
    status TEXT NOT NULL,
    outcome TEXT,
    summary TEXT,
    note TEXT,
    started_at TEXT NOT NULL,
    finished_at TEXT
);

CREATE INDEX IF NOT EXISTS ix_workflow_run_steps_run ON workflow_run_steps(run_id, started_at);
CREATE INDEX IF NOT EXISTS ix_workflow_run_steps_session ON workflow_run_steps(session_id);

ALTER TABLE sessions ADD COLUMN workflow_run_id TEXT;
