// What's typed in an open session's composer, kept on the phone per machine + session so killing or reloading the app
// doesn't lose it (the web client keeps drafts the same way, lib/draft-storage). Pure: the storage comes in from outside.
import type { KeyValueStorage } from "~/fleet/machines-core";

// SecureStore keys allow letters, digits, ".", "-" and "_".
const safe = (value: string) => value.replace(/[^A-Za-z0-9._-]/g, "_");
export const draftKey = (machineId: string, sessionId: string) => `fleet.draft.${safe(machineId)}.${safe(sessionId)}`;

export class DraftsStore {
  constructor(private storage: KeyValueStorage) {}

  /** The kept draft, or "" when there is none or storage can't be read. */
  async read(machineId: string, sessionId: string): Promise<string> {
    try {
      return (await this.storage.get(draftKey(machineId, sessionId))) ?? "";
    } catch {
      return "";
    }
  }

  /** Keeps `text`; an empty draft removes the key. Storage failures leave the draft in memory only. */
  async write(machineId: string, sessionId: string, text: string): Promise<void> {
    const key = draftKey(machineId, sessionId);
    try {
      if (text) await this.storage.set(key, text);
      else await this.storage.remove(key);
    } catch {
      // In memory only.
    }
  }
}
