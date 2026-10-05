-- Migration 052: Where this machine sends notifications.
-- One row per Web Push subscription (a phone or browser that turned notifications on). channel says how a push
-- gets there: 'webpush' now, a native app's channel ('apns', 'fcm') later. endpoint is a capability URL and is unique.
-- device_id is the paired device that subscribed (no foreign key: removing a device deletes its rows in code).
-- kinds is a JSON array of the notifications it wants; quiet_when_desk skips pushes while Fleet is open on a computer.
-- failure_count counts failed sends in a row; the row goes after 10.

CREATE TABLE IF NOT EXISTS push_subscriptions (
  id TEXT PRIMARY KEY,
  device_id TEXT,
  channel TEXT NOT NULL DEFAULT 'webpush',
  endpoint TEXT NOT NULL UNIQUE,
  p256dh TEXT NOT NULL,
  auth TEXT NOT NULL,
  kinds TEXT NOT NULL DEFAULT '[]',
  quiet_when_desk INTEGER NOT NULL DEFAULT 0,
  user_agent TEXT,
  created_at TEXT NOT NULL,
  last_success_at TEXT,
  failure_count INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS ix_push_subscriptions_device ON push_subscriptions (device_id);
