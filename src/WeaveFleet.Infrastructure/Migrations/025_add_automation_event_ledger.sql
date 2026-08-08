CREATE TABLE IF NOT EXISTS automation_event_ledger (
  automation_id TEXT NOT NULL,
  source_event_id TEXT NOT NULL,
  processed_at TEXT NOT NULL,
  PRIMARY KEY (automation_id, source_event_id)
);
