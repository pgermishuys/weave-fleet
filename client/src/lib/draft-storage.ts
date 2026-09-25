/**
 * Composer drafts kept in the browser, so a reload doesn't lose what was typed. Every read and write is guarded:
 * storage can be full, blocked or missing (private windows, some embeds), and then drafts are kept in memory only.
 *
 * Keys, under `weave-fleet.draft.`:
 * - `session.<sessionId>`: what's in the session's composer.
 * - `side.<sessionId>.<sideConversationId>`: a side conversation's (`/btw`) draft while the composer talks to the session.
 * - `side-main.<sessionId>`: the session's draft while the composer talks to its side conversation.
 */
const PREFIX = "weave-fleet.draft.";

export function readStoredDraft(key: string): string | null {
  try {
    return globalThis.localStorage?.getItem(PREFIX + key) ?? null;
  } catch {
    return null;
  }
}

/** Keeps `text` under `key`; an empty draft removes the key. */
export function writeStoredDraft(key: string, text: string): void {
  try {
    if (text) {
      globalThis.localStorage?.setItem(PREFIX + key, text);
    } else {
      globalThis.localStorage?.removeItem(PREFIX + key);
    }
  } catch {
    // Kept in memory only.
  }
}

/** The keys (without the prefix) that start with `start`. */
export function storedDraftKeys(start: string): string[] {
  try {
    const storage = globalThis.localStorage;
    if (!storage) return [];
    const keys: string[] = [];
    for (let index = 0; index < storage.length; index += 1) {
      const key = storage.key(index);
      if (key?.startsWith(PREFIX + start)) keys.push(key.slice(PREFIX.length));
    }
    return keys;
  } catch {
    return [];
  }
}

export const sessionDraftKey = (sessionId: string) => `session.${sessionId}`;
export const sideDraftKey = (sessionId: string, sideConversationId: string) => `side.${sessionId}.${sideConversationId}`;
export const sideMainDraftKey = (sessionId: string) => `side-main.${sessionId}`;
