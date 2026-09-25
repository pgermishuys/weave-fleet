-- Migration 045: Which answer of a side conversation (/btw) the user has seen.
-- side_seen_answer_id is the id of the newest answer the user had in front of them with the panel open. A newer
-- finished answer shows the minimized tab as New answer, after a reload or with the page closed in between.

ALTER TABLE sessions ADD COLUMN side_seen_answer_id TEXT;
