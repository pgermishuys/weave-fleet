import type { HandleKind } from "../tree";

export interface HandleEntry {
  id: string;
  owner: string;
  kind: HandleKind;
  key: string;
  fn: (...args: any[]) => unknown;
  sessionId: string;
  /** component + requestId */
  site: string;
}

export interface Handles {
  alloc(entry: Omit<HandleEntry, "id">): string;
  get(id: string): HandleEntry | undefined;
  /** The handles drawn for this site now, so they can be dropped once the new drawing has its own. */
  ofSite(sessionId: string, site: string): string[];
  drop(ids: Iterable<string>): void;
  dropMod(owner: string): void;
  dropSession(sessionId: string): void;
}

/** The callbacks of drawn trees, by handle (`h1`, `h2`…). */
export function createHandles(): Handles {
  const entries = new Map<string, HandleEntry>();
  let counter = 0;
  return {
    alloc(entry) {
      const id = `h${++counter}`;
      entries.set(id, { ...entry, id });
      return id;
    },
    get: (id) => entries.get(id),
    ofSite(sessionId, site) {
      return [...entries.values()].filter((h) => h.sessionId === sessionId && h.site === site).map((h) => h.id);
    },
    drop(ids) {
      for (const id of ids) entries.delete(id);
    },
    dropMod(owner) {
      for (const [id, h] of [...entries]) if (h.owner === owner) entries.delete(id);
    },
    dropSession(sessionId) {
      for (const [id, h] of [...entries]) if (h.sessionId === sessionId) entries.delete(id);
    },
  };
}
