-- Migration 056: Pinned sessions.
-- A pinned session sits in a Pinned group above the projects in the Sessions list, in the order the user dragged it to.
-- pin_order is that order (ascending; fractional so a drag rewrites only the row that moved); NULL means not pinned.
-- Archiving a session unpins it.

ALTER TABLE sessions ADD COLUMN pin_order REAL;
