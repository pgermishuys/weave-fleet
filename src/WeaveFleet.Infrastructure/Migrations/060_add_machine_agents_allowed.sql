-- Migration 060: Agents may hand work to a machine only when the owner allows it.
-- machines.agents_allowed is 1 when an agent in a session here may start, message and read sessions on that machine
-- (fleet_session_start, and fleet_message / fleet_session_read with a machine), with "Hand work to other machines" on.
-- 0 for every machine already in the list, and for each one added: the owner turns it on per machine.

ALTER TABLE machines ADD COLUMN agents_allowed INTEGER NOT NULL DEFAULT 0;
