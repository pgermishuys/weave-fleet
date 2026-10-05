-- Migration 050: Moving a session out of the session it came from.
-- forked_from_session_id and spawned_by_session_id stay as what happened; lineage_detached_at says the user has since
-- moved the session out to stand on its own. A detached session doesn't nest under its parent, isn't one of the
-- parent's agents, and counts as one the user started (an agent in it may start sessions again).

ALTER TABLE sessions ADD COLUMN lineage_detached_at TEXT;
