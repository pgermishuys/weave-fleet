-- Migration 072: One setting per machine for agents here sending it work: ask (the default), always or never.
-- It replaces agents_allowed, which stays unused: a machine allowed before is "always"; the rest "ask", since nobody
-- chose to keep agents out before (the box was off until ticked).

ALTER TABLE machines ADD COLUMN agent_work TEXT NOT NULL DEFAULT 'ask';
UPDATE machines SET agent_work = 'always' WHERE agents_allowed = 1;
