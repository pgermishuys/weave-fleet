-- Migration 033: Automation time zones.
-- The IANA zone a schedule's cron expression is read in. Existing rows stay NULL, which the scheduler
-- reads as UTC, so automations made before this keep firing at the times they always have.

ALTER TABLE automations ADD COLUMN time_zone TEXT;
