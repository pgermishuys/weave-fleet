-- Migration 059: Automations name the machine they run on.
-- automations.target_machine_id is a machine in this Fleet's list (remote_machines.id) whose API starts each run's
-- session or workflow run; the automation, its schedule and its runs stay here. NULL means this machine, which is how
-- every automation made before ran. 'any' is reserved for Fleet picking a machine, not stored yet.
-- automation_runs.machine_id and machine_name say which machine a run went to; the name is kept as it was then, so the
-- run still reads right after the machine is renamed or removed. NULL for runs on this machine.
-- automation_runs.settled_state is how a run on another machine ended ('done', 'ended' or 'failed'), once that machine
-- said so; it isn't asked again. NULL while it may still be going, and for runs on this machine.

ALTER TABLE automations ADD COLUMN target_machine_id TEXT;
ALTER TABLE automation_runs ADD COLUMN machine_id TEXT;
ALTER TABLE automation_runs ADD COLUMN machine_name TEXT;
ALTER TABLE automation_runs ADD COLUMN settled_state TEXT;
