-- Migration 034: Automation runs, and where a run happens.
-- isolation is how a run gets its folder: 'worktree' (a new worktree of the folder each run) or 'existing' (the folder
-- as it is). NULL means the automation was made before this and runs the way it always has. base_branch is where a
-- worktree starts; NULL means the repository's default.
-- automation_runs keeps one row per run Fleet started, failed to start or skipped. status is what Fleet did:
-- 'starting', 'started', 'failed' or 'skipped'; whether a started run is still going comes from its session.
-- scheduled_for is the schedule occurrence a run was for, which is how the scheduler knows what it has handled after
-- a restart.

ALTER TABLE automations ADD COLUMN isolation TEXT;
ALTER TABLE automations ADD COLUMN base_branch TEXT;

CREATE TABLE IF NOT EXISTS automation_runs (
  id TEXT PRIMARY KEY,
  automation_id TEXT NOT NULL,
  user_id TEXT NOT NULL,
  trigger TEXT NOT NULL,
  scheduled_for TEXT,
  started_at TEXT NOT NULL,
  status TEXT NOT NULL,
  session_id TEXT,
  instance_id TEXT,
  error TEXT
);

CREATE INDEX IF NOT EXISTS ix_automation_runs_automation ON automation_runs(automation_id, started_at DESC);
