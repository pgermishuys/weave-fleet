-- Migration 048: Running work, and which session started which.
-- A delegation becomes one kind of running work: anything an agent left running that Fleet can show, stop or read.
-- Existing rows are subagents and keep working as before. Columns that already meant the same thing keep their names:
-- parent_tool_call_id is the call that started the work, created_at when it started, completed_at when it ended.
--   kind            subagent | shell | monitor | task
--   work_id         the harness's own handle for it (a shell id, a task id, the call id of a subagent)
--   label           what it is: the subagent's task, the shell's command
--   background      1 when the call that started it returned while the work carries on
--   can_stop        1 when the harness can stop this item on its own
--   can_read_output 1 when the harness can page through its output
--   ended_reason    completed | error | cancelled | lost (Fleet or the harness stopped and the work went with it)
--   detail          how it ended, in a few words (exit 0)
-- Lineage on sessions; parent_session_id still means "a hidden delegated child", and side_of_session_id a /btw.
--   forked_from_session_id  the session this one is a fork of
--   spawned_by_session_id   the session whose agent started this one
--   spawn_kind              fork | api | message | automation | workflow

ALTER TABLE delegations ADD COLUMN kind TEXT NOT NULL DEFAULT 'subagent';
ALTER TABLE delegations ADD COLUMN work_id TEXT;
ALTER TABLE delegations ADD COLUMN label TEXT;
ALTER TABLE delegations ADD COLUMN background INTEGER NOT NULL DEFAULT 0;
ALTER TABLE delegations ADD COLUMN can_stop INTEGER NOT NULL DEFAULT 0;
ALTER TABLE delegations ADD COLUMN can_read_output INTEGER NOT NULL DEFAULT 0;
ALTER TABLE delegations ADD COLUMN ended_reason TEXT;
ALTER TABLE delegations ADD COLUMN detail TEXT;

UPDATE delegations SET work_id = COALESCE(parent_tool_call_id, id) WHERE work_id IS NULL;
UPDATE delegations SET ended_reason = status WHERE status IN ('completed', 'error', 'cancelled') AND ended_reason IS NULL;

CREATE INDEX IF NOT EXISTS idx_delegations_parent_work_id ON delegations(parent_session_id, work_id);
CREATE INDEX IF NOT EXISTS idx_delegations_running ON delegations(status) WHERE status IN ('pending', 'running');

ALTER TABLE sessions ADD COLUMN forked_from_session_id TEXT;
ALTER TABLE sessions ADD COLUMN spawned_by_session_id TEXT;
ALTER TABLE sessions ADD COLUMN spawn_kind TEXT;

CREATE INDEX IF NOT EXISTS idx_sessions_forked_from_session_id ON sessions(forked_from_session_id);
CREATE INDEX IF NOT EXISTS idx_sessions_spawned_by_session_id ON sessions(spawned_by_session_id);
