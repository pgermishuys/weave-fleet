import type { Json } from "./types";
import { deepFreeze, parseJson, toJsonText } from "./freeze";

type Key = string;
const scope = (mod: string, session: string): Key => `${mod}\u0000${session}`;

interface Slot {
  values: Map<string, string>;
  bytes: number;
  /** Keys a ui.render hook read: a write to one redraws the mod in this session. */
  subscribed: Set<string>;
}

export interface StateStore {
  get(mod: string, session: string, key: string, subscribe: boolean): Json | undefined;
  /** Stores `value`; throws if it isn't JSON or the mod's state would pass the limit. Returns true when the key is subscribed. */
  set(mod: string, session: string, key: string, value: unknown): boolean;
  dropMod(mod: string): void;
  dropSession(session: string): void;
}

const size = (key: string, text: string) => Buffer.byteLength(key) + Buffer.byteLength(text);

/** `$.state`: JSON values per mod id per session, kept in the host. */
export function createStateStore(limitBytes: () => number): StateStore {
  const slots = new Map<Key, Slot>();
  const slotFor = (mod: string, session: string) => {
    const k = scope(mod, session);
    let s = slots.get(k);
    if (!s) slots.set(k, (s = { values: new Map(), bytes: 0, subscribed: new Set() }));
    return s;
  };

  return {
    get(mod, session, key, subscribe) {
      const slot = subscribe ? slotFor(mod, session) : slots.get(scope(mod, session));
      if (subscribe) slot!.subscribed.add(key);
      const text = slot?.values.get(key);
      return text === undefined ? undefined : deepFreeze(parseJson(text));
    },
    set(mod, session, key, value) {
      const text = toJsonText(value);
      const slot = slotFor(mod, session);
      const previous = slot.values.get(key);
      const bytes = slot.bytes - (previous === undefined ? 0 : size(key, previous)) + size(key, text);
      if (bytes > limitBytes()) throw new RangeError(`$.state is over its ${limitBytes()} byte limit`);
      slot.values.set(key, text);
      slot.bytes = bytes;
      return slot.subscribed.has(key);
    },
    dropMod(mod) {
      for (const k of [...slots.keys()]) if (k.startsWith(`${mod}\u0000`)) slots.delete(k);
    },
    dropSession(session) {
      for (const k of [...slots.keys()]) if (k.endsWith(`\u0000${session}`)) slots.delete(k);
    },
  };
}
