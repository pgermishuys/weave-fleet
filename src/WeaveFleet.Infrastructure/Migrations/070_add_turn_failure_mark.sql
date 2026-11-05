-- Migration 070: A session whose last turn failed.
-- turn_failed_at / turn_failed_message are set when a turn stops on a provider or harness error (turn.failed) and
-- cleared when the next turn starts, so the sessions list can show the failure without opening the session.

ALTER TABLE sessions ADD COLUMN turn_failed_at TEXT;
ALTER TABLE sessions ADD COLUMN turn_failed_message TEXT;
