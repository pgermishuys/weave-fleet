-- Migration 069: Built-in automations, and runs that say what they did in words.
-- automations.builtin names an automation Fleet owns ('dreaming'): Fleet creates and removes it, and it can't be edited
-- or deleted through the Automations API. NULL for every automation a person made.
-- automation_runs.summary is what a run that starts several sessions did, in words ("orchard: 2 notes changed ·
-- 41k tokens"); a night of dreaming writes it as it goes and settles the run (settled_state) when it ends.

ALTER TABLE automations ADD COLUMN builtin TEXT;
ALTER TABLE automation_runs ADD COLUMN summary TEXT;
