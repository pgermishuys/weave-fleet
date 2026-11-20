-- Migration 077: the Claude Code transcript entries Fleet itself produced in a session.
-- A Claude Code session started in a terminal can be continued from Fleet and back: every turn ends up in one transcript
-- (~/.claude/projects/<folder>/<session id>.jsonl). Fleet saves its own turns as it runs them, so when it reads the
-- transcript for turns typed in the terminal it must leave these entries out. transcript_claims holds their uuids.
CREATE TABLE IF NOT EXISTS transcript_claims (
    session_id TEXT NOT NULL,
    entry_uuid TEXT NOT NULL,
    PRIMARY KEY (session_id, entry_uuid),
    FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE CASCADE
);
