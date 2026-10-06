-- Migration 054: A prompt the user sent into a running turn (steered), for harnesses whose history Fleet keeps.
-- Claude Code doesn't write a steered message back, so Fleet saves it where it was sent; without this, the
-- conversation lost the "sent while working" mark on a reload.

ALTER TABLE messages ADD COLUMN steered INTEGER NOT NULL DEFAULT 0;
