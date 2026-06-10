ALTER TABLE sessions ADD COLUMN runtime_mode TEXT NOT NULL DEFAULT 'manual';

UPDATE sessions
SET runtime_mode = 'manual'
WHERE runtime_mode IS NULL OR runtime_mode = '';
