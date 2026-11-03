// The phone's own memory of where it started sessions, in the Keychain/Keystore-backed storage (as the machines are).
import { storage } from "~/fleet/machines";
import { NewSessionRecents } from "~/fleet/new-session-core";

export const newSessionRecents = new NewSessionRecents(storage);
