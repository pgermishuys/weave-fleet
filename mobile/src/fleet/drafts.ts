// The phone's kept composer drafts, in the same Keychain/Keystore-backed storage as the machines.
import { storage } from "~/fleet/machines";
import { DraftsStore } from "~/fleet/drafts-core";

export const drafts = new DraftsStore(storage);
