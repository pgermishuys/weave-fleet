// When the phone last opened each session's Pages, kept on the phone: what makes a page "new" on the session's row
// ("2 new pages") and in its Pages list. Pure: the storage (Keychain/Keystore on a phone) comes in from outside.
import type { KeyValueStorage } from "~/fleet/machines-core";

/** When the phone last opened a session's Pages, and how many page tabs the session had then. */
export interface PagesSeen {
  at: string;
  count: number;
}

const SINCE_KEY = "fleet.pages-seen.since";
// SecureStore keys allow letters, digits, ".", "-" and "_".
const safe = (value: string) => value.replace(/[^A-Za-z0-9._-]/g, "_");
const keyOf = (machineId: string, sessionId: string) => `fleet.pages-seen.${safe(machineId)}.${safe(sessionId)}`;
const cacheKey = (machineId: string, sessionId: string) => `${machineId}\n${sessionId}`;

/**
 * How many of a session's pages are new to the phone, from the session list's count and newest page: none when the
 * newest was shown before the phone last opened the session's Pages, else the pages added since, and at least one
 * (the agent showed a page again). A session the phone never opened counts from when it started keeping track.
 */
export function newPagesOf(item: { pageCount?: number; lastPageShownAt?: string | null }, seen: PagesSeen | null | undefined, since: string): number {
  const count = item.pageCount ?? 0;
  const last = item.lastPageShownAt;
  if (!count || !last) return 0;
  const { at, count: seenCount } = seen ?? { at: since, count: 0 };
  return last > at ? Math.max(1, count - seenCount) : 0;
}

/**
 * Each session's {@link PagesSeen} under its own key, read on demand (SecureStore has no listing) and cached, so the
 * list renders synchronously and fills in as reads land. `since` is when the phone started keeping track: pages shown
 * before it aren't new, so pairing a machine doesn't light up every old session.
 */
export class PagesSeenStore {
  private seen = new Map<string, PagesSeen | null>();
  private reading = new Set<string>();
  private listeners = new Set<() => void>();
  private version = 0;
  since = new Date().toISOString();

  constructor(private storage: KeyValueStorage, private now: () => Date = () => new Date()) {}

  async load(): Promise<void> {
    const kept = await this.storage.get(SINCE_KEY);
    if (kept) this.since = kept;
    else {
      this.since = this.now().toISOString();
      await this.storage.set(SINCE_KEY, this.since);
    }
    this.changed();
  }

  /** What's known now; `undefined` while it's being read (the read starts here). */
  get(machineId: string, sessionId: string): PagesSeen | null | undefined {
    const key = cacheKey(machineId, sessionId);
    if (this.seen.has(key)) return this.seen.get(key);
    if (!this.reading.has(key)) {
      this.reading.add(key);
      void this.storage.get(keyOf(machineId, sessionId))
        .then((raw) => this.seen.set(key, raw ? (JSON.parse(raw) as PagesSeen) : null))
        .catch(() => this.seen.set(key, null))
        .finally(() => { this.reading.delete(key); this.changed(); });
    }
    return undefined;
  }

  /** The phone opened the session's Pages, which had `count` page tabs. */
  async markSeen(machineId: string, sessionId: string, count: number): Promise<PagesSeen> {
    const seen = { at: this.now().toISOString(), count };
    this.seen.set(cacheKey(machineId, sessionId), seen);
    this.changed();
    await this.storage.set(keyOf(machineId, sessionId), JSON.stringify(seen));
    return seen;
  }

  subscribe = (listener: () => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  /** Changes whenever anything known changes, for useSyncExternalStore. */
  snapshot = () => this.version;

  private changed() {
    this.version++;
    for (const listener of this.listeners) listener();
  }
}
