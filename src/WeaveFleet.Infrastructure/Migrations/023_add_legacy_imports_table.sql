-- Migration 023: Legacy imports marker table.
-- Tracks import runs from legacy sources so completed or failed imports can be
-- audited and skipped on subsequent attempts.

CREATE TABLE IF NOT EXISTS legacy_imports (
    id            TEXT    NOT NULL PRIMARY KEY,
    source_path   TEXT,
    imported_at   TEXT,
    session_count INTEGER,
    status        TEXT
);
