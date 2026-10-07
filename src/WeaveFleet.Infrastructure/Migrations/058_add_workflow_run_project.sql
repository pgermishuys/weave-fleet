-- Migration 058: Choose a run's project before it starts.
-- workflow_runs.project_id is the project picked in the Run box, or NULL for Scratch. The first step's session goes
-- there; later steps follow whichever project the run's sessions are actually in (so moving the run is still
-- respected), falling back to project_id only while no step session has one yet.

ALTER TABLE workflow_runs ADD COLUMN project_id TEXT;
