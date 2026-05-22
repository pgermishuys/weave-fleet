-- Store the session's git baseline metadata captured at creation time.
-- Nullable by design so existing sessions are unaffected.
ALTER TABLE sessions ADD COLUMN git_baseline_ref TEXT;
ALTER TABLE sessions ADD COLUMN git_repo_root TEXT;
