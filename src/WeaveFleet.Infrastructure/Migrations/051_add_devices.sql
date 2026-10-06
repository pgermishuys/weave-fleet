-- Migration 051: Devices with their own access token, such as a phone paired by QR code.
-- Each device gets a token of its own, so removing one leaves every other device alone. Only a SHA-256 hash of the
-- token's secret is kept; the token itself goes to the device once and Fleet never sees it again in the clear.
-- last_used_at drives the 30-day expiry: a device that hasn't been used for 30 days stops working.
-- paired_via is the id of the machine that asked for this token on the device's behalf (a phone paired with another
-- machine, its home machine); null when the device paired here.

CREATE TABLE IF NOT EXISTS devices (
  id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  platform TEXT,
  token_hash BLOB NOT NULL,
  paired_via TEXT,
  created_at TEXT NOT NULL,
  last_used_at TEXT NOT NULL,
  revoked_at TEXT
);

CREATE INDEX IF NOT EXISTS ix_devices_revoked_at ON devices (revoked_at);
