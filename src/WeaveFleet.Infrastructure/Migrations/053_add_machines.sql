-- Migration 053: The machines this Fleet knows, kept on the server instead of only in one browser.
-- A phone's home machine reads this list to push notifications for every machine and to get the phone its own token
-- on each one. encrypted_token is the other machine's access token, encrypted with Data Protection. status is what the
-- home server last saw: 'unknown', 'online', 'unreachable' or 'unauthorized'.
-- device_grants records which device token another machine made for one of this machine's paired devices
-- (remote_device_id is that machine's id for it), so removing the phone here can remove it there. revoked_at marks a
-- grant whose removal on the other machine hasn't gone through yet; the row goes once it has.

CREATE TABLE IF NOT EXISTS machines (
  id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  base_url TEXT NOT NULL,
  encrypted_token TEXT NOT NULL,
  os TEXT,
  added_at TEXT NOT NULL,
  last_seen_at TEXT,
  status TEXT NOT NULL DEFAULT 'unknown'
);

CREATE TABLE IF NOT EXISTS device_grants (
  device_id TEXT NOT NULL,
  machine_id TEXT NOT NULL,
  remote_device_id TEXT NOT NULL,
  created_at TEXT NOT NULL,
  revoked_at TEXT,
  PRIMARY KEY (device_id, machine_id)
);

CREATE INDEX IF NOT EXISTS ix_device_grants_machine ON device_grants (machine_id);
