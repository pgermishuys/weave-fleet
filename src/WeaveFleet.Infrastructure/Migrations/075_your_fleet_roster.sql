-- Migration 075: Your Fleet roster copy (Labs).
-- devices.created_by is the device that asked for this token (another machine's key on this one); null when the owner
-- or a pairing code made it. A machine may remove only the devices it made.
-- machines.introduced_by is the id of the machine that introduced this one (the roster was copied from it); null when
-- it was added here directly.
ALTER TABLE devices ADD COLUMN created_by TEXT;
ALTER TABLE machines ADD COLUMN introduced_by TEXT;
