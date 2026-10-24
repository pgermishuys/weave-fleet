-- Migration 066: What a device is.
-- A device is a phone or browser; a machine is another Fleet that paired with a one-time code (fleet node pair).
-- Settings lists both and removes either one.

ALTER TABLE devices ADD COLUMN kind TEXT NOT NULL DEFAULT 'device';
