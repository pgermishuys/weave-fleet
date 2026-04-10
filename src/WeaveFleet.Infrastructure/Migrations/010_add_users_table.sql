-- Migration 010: Add users table
-- Creates shadow user records matched to IdP identities.

CREATE TABLE IF NOT EXISTS users (
    id                       TEXT    NOT NULL PRIMARY KEY,   -- IdP "sub" claim
    email                    TEXT    NOT NULL,
    display_name             TEXT,
    status                   TEXT    NOT NULL DEFAULT 'active',
    created_at               TEXT    NOT NULL,
    last_login_at            TEXT,
    onboarding_completed_at  TEXT
);

CREATE INDEX IF NOT EXISTS idx_users_email ON users(email);
