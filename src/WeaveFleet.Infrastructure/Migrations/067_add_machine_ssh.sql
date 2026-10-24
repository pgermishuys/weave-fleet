-- Migration 067: How a machine is reached over SSH.
-- A machine added "Over SSH" keeps the destination (an ~/.ssh/config alias or user@host[:port]) so Fleet can open its
-- tunnel again after a restart. Null for a machine reached by its own address.

ALTER TABLE machines ADD COLUMN ssh_destination TEXT;
