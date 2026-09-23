-- Migration 040: Fleet commits a step's declared files once the files check passes.
-- files_commit is the short SHA of that commit; NULL when there was nothing to commit (the agent had committed them,
-- or git ignores them) or the commit failed. files_commit_error is why it failed, in git's words; a failure never
-- stops the run.

ALTER TABLE workflow_run_steps ADD COLUMN files_commit TEXT;
ALTER TABLE workflow_run_steps ADD COLUMN files_commit_error TEXT;
