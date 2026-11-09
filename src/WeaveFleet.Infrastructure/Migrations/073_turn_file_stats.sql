-- Migration 073: What each turn left the session's files at, taken as the turn ended.
-- A whole-file write sends only the new contents, so what it changed is the file's change against the session's
-- baseline at the moment its turn ended. The server keeps that here, one set of rows per turn end (ended_at), so a
-- turn's card shows the same counts in every browser, on phones and from other machines, after later turns change the
-- same files. A snapshot with no changed files is one row whose file is the empty string.

CREATE TABLE IF NOT EXISTS turn_file_stats (
  session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  ended_at TEXT NOT NULL,
  file TEXT NOT NULL,
  additions INTEGER NOT NULL DEFAULT 0,
  deletions INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (session_id, ended_at, file)
);
