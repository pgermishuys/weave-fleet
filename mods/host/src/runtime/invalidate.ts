import type { Peer } from "./types";

interface Entry {
  lastSent: number;
  timer?: ReturnType<typeof setTimeout>;
}

export interface Invalidator {
  request(mod: string, session: string | undefined): void;
  dropMod(mod: string): void;
  dropSession(session: string): void;
  stopAll(): void;
}

const key = (mod: string, session: string | undefined) => `${mod}\u0000${session ?? ""}`;

/**
 * Sends `invalidate` at most once per window per mod per session: the first call goes at once, calls inside the
 * window coalesce into one sent when it ends.
 */
export function createInvalidator(peer: Peer, perSecond: () => number, now: () => number): Invalidator {
  const entries = new Map<string, Entry>();

  const send = (mod: string, session: string | undefined) => {
    peer.notify("invalidate", session === undefined ? { mod } : { mod, sessionId: session });
  };

  return {
    request(mod, session) {
      const k = key(mod, session);
      let entry = entries.get(k);
      if (!entry) entries.set(k, (entry = { lastSent: Number.NEGATIVE_INFINITY }));
      if (entry.timer) return;
      const window = 1000 / perSecond();
      const wait = entry.lastSent + window - now();
      if (wait <= 0) {
        entry.lastSent = now();
        send(mod, session);
        return;
      }
      const e = entry;
      e.timer = setTimeout(() => {
        e.timer = undefined;
        e.lastSent = now();
        send(mod, session);
      }, wait);
    },
    dropMod(mod) {
      for (const [k, e] of [...entries]) if (k.startsWith(`${mod}\u0000`)) (clearTimeout(e.timer), entries.delete(k));
    },
    dropSession(session) {
      for (const [k, e] of [...entries]) if (k.endsWith(`\u0000${session}`)) (clearTimeout(e.timer), entries.delete(k));
    },
    stopAll() {
      for (const e of entries.values()) clearTimeout(e.timer);
      entries.clear();
    },
  };
}
