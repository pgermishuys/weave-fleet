-- Migration 029: Smart link relationship and enrichment tracking.
-- relationship: how the link relates to its session (origin, own, pinned, mentioned).
-- enrichment_status: whether live details were fetched (pending, resolved, not_connected, not_found, error).
-- Existing rows were resolved by the client, so they start as 'resolved'.

ALTER TABLE smart_links ADD COLUMN relationship TEXT NOT NULL DEFAULT 'mentioned';
ALTER TABLE smart_links ADD COLUMN enrichment_status TEXT NOT NULL DEFAULT 'resolved';
ALTER TABLE smart_links ADD COLUMN last_checked_at TEXT;

-- A NULL last_checked_at means "check now"; existing rows were checked when last updated,
-- so upgrading doesn't re-fetch every link at once.
UPDATE smart_links SET last_checked_at = updated_at;

UPDATE smart_links
SET relationship = 'origin'
WHERE EXISTS (
    SELECT 1 FROM session_source_usages u
    WHERE u.session_id = smart_links.session_id
      AND u.resource_url = smart_links.url
      AND u.action_id = 'start-session'
);

UPDATE smart_links
SET relationship = 'pinned'
WHERE relationship = 'mentioned'
  AND EXISTS (
    SELECT 1 FROM session_source_usages u
    WHERE u.session_id = smart_links.session_id
      AND u.resource_url = smart_links.url
      AND u.action_id = 'add-to-session'
);
