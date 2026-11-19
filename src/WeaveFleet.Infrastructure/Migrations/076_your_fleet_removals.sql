-- Migration 076: Your Fleet removals (Labs).
-- devices.made_for is what a key was made for: the machine whose key it is (a key another machine uses to reach this
-- one) or the phone/screen it is a grant for (a key a home asked this machine for). Forgetting that machine or removing
-- that phone revokes every key made for it. Null for a key the owner or a pairing code made.
-- fleet_removals keeps what was forgotten or removed, so a stale list from a machine that was off can't bring it back:
-- kind 'machine' (a machine forgotten from the Fleet) or 'grantee' (a phone or screen removed from the Fleet).
ALTER TABLE devices ADD COLUMN made_for TEXT;
CREATE TABLE IF NOT EXISTS fleet_removals (
    id TEXT PRIMARY KEY,
    kind TEXT NOT NULL,
    removed_at TEXT NOT NULL,
    removed_by TEXT
);
