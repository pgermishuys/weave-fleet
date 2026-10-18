-- Migration 064: Drop diagram and sequence canvases.
-- Fleet no longer draws these kinds (diagrams are HTML pages now), so the saved ones go. Revisions first,
-- because deleting the canvases only cascades when foreign keys are on.

DELETE FROM canvas_revisions
WHERE canvas_id IN (SELECT id FROM canvases WHERE kind IN ('diagram', 'sequence'));

DELETE FROM canvases WHERE kind IN ('diagram', 'sequence');
