-- Migration 068: Jobs, one queue of work that free seats on any machine pick up (Experimental).
-- A job's seat is the machine it was handed to; seats and labels are kept per machine, this one included.

CREATE TABLE jobs (
    id TEXT PRIMARY KEY,
    user_id TEXT NOT NULL,
    title TEXT NOT NULL,
    ask TEXT NOT NULL,
    repository TEXT NOT NULL,
    labels TEXT NOT NULL DEFAULT '[]',
    pinned_machine_id TEXT,
    state TEXT NOT NULL DEFAULT 'waiting',
    machine_id TEXT,
    machine_name TEXT,
    session_id TEXT,
    worked INTEGER NOT NULL DEFAULT 0,
    error TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE INDEX idx_jobs_user_state ON jobs(user_id, state, created_at);

-- Machines with no row take no work, except this one, which counts as two seats until its row says otherwise.
CREATE TABLE job_machine_settings (
    machine_id TEXT PRIMARY KEY,
    seats INTEGER NOT NULL DEFAULT 0,
    labels TEXT NOT NULL DEFAULT '[]'
);
