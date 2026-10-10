import type { Json } from "./types";
import { deepFreeze, parseJson, toJsonText } from "./freeze";

type Key = string;
const scope = (name: string, session: string): Key => `${name}\u0000${session}`;

interface Slot {
  values: Map<string, string>;
  bytes: number;
  /** Per render site, the keys its last render read: a write to one redraws the mod in this session. */
  subscribed: Map<string, Set<string>>;
}

export interface StateStore {
  /** Reads `key`; with a `site`, a render there read it, so a write redraws the mod. */
  get(name: string, session: string, key: string, site?: string): Json | undefined;
  /** Stores `value`; throws if it isn't JSON or the mod's state would pass the limit. Returns true when the key is subscribed. */
  set(name: string, session: string, key: string, value: unknown): boolean;
  /** A render of `site` begins: it subscribes afresh to what it reads. */
  resetSite(name: string, session: string, site: string): void;
  dropSession(session: string): void;
}

const size = (key: string, text: string) => Buffer.byteLength(key) + Buffer.byteLength(text);

/**
 * `$.state`: JSON values per mod name per session, kept in the host. By name, not id, so a reload, Keep (draft to
 * version) and Undo (version to version) keep the state the drawing depends on.
 */
export function createStateStore(limitBytes: () => number): StateStore {
  const slots = new Map<Key, Slot>();
  const slotFor = (name: string, session: string) => {
    const k = scope(name, session);
    let s = slots.get(k);
    if (!s) slots.set(k, (s = { values: new Map(), bytes: 0, subscribed: new Map() }));
    return s;
  };

  return {
    get(name, session, key, site) {
      const slot = site !== undefined ? slotFor(name, session) : slots.get(scope(name, session));
      if (site !== undefined) {
        let keys = slot!.subscribed.get(site);
        if (!keys) slot!.subscribed.set(site, (keys = new Set()));
        keys.add(key);
      }
      const text = slot?.values.get(key);
      return text === undefined ? undefined : deepFreeze(parseJson(text));
    },
    set(name, session, key, value) {
      const text = toJsonText(value);
      const slot = slotFor(name, session);
      const previous = slot.values.get(key);
      const bytes = slot.bytes - (previous === undefined ? 0 : size(key, previous)) + size(key, text);
      if (bytes > limitBytes()) throw new RangeError(`$.state is over its ${limitBytes()} byte limit`);
      slot.values.set(key, text);
      slot.bytes = bytes;
      for (const keys of slot.subscribed.values()) if (keys.has(key)) return true;
      return false;
    },
    resetSite(name, session, site) {
      slots.get(scope(name, session))?.subscribed.delete(site);
    },
    dropSession(session) {
      for (const k of [...slots.keys()]) if (k.endsWith(`\u0000${session}`)) slots.delete(k);
    },
  };
}
