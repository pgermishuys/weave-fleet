/**
 * Messages typed on the phone while the machine is away: held on the phone, shown as "Held", and sent in order when
 * the machine answers again. Never silently dropped: one that fails to send stays held with the reason, to retry,
 * edit or remove. Kept in localStorage per machine and session. Permission answers are never held: they're
 * time-sensitive, so those fail out loud instead. No Vue.
 */

export interface HeldMessage {
  id: string;
  text: string;
  heldAt: number;
  /** Why the last try didn't send, when one didn't. */
  error?: string;
}

const KEY = "weave:phone-outbox";

type Store = Record<string, HeldMessage[]>;

function storeKey(machineId: string, sessionId: string): string {
  return `${machineId}:${sessionId}`;
}

function read(): Store {
  try {
    const parsed: unknown = JSON.parse(localStorage.getItem(KEY) ?? "{}");
    return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? parsed as Store : {};
  } catch {
    return {};
  }
}

function write(store: Store): void {
  const kept = Object.fromEntries(Object.entries(store).filter(([, items]) => items.length > 0));
  try {
    localStorage.setItem(KEY, JSON.stringify(kept));
  } catch {
    // Storage is full or off: what's held lasts until the page closes, which is still better than dropping it.
  }
}

export function heldFor(machineId: string, sessionId: string): HeldMessage[] {
  return read()[storeKey(machineId, sessionId)] ?? [];
}

export function hold(machineId: string, sessionId: string, text: string, now = Date.now()): HeldMessage {
  const store = read();
  const key = storeKey(machineId, sessionId);
  const item: HeldMessage = { id: `${now}-${Math.random().toString(36).slice(2, 8)}`, text, heldAt: now };
  store[key] = [...(store[key] ?? []), item];
  write(store);
  return item;
}

export function editHeld(machineId: string, sessionId: string, id: string, text: string): void {
  const store = read();
  const key = storeKey(machineId, sessionId);
  store[key] = (store[key] ?? []).map((item) => (item.id === id ? { ...item, text, error: undefined } : item));
  write(store);
}

export function removeHeld(machineId: string, sessionId: string, id: string): void {
  const store = read();
  const key = storeKey(machineId, sessionId);
  store[key] = (store[key] ?? []).filter((item) => item.id !== id);
  write(store);
}

/**
 * Sends what's held, oldest first, with `send`. Stops at the first one that doesn't go, so the order holds; that one
 * keeps its reason. Returns what's still held.
 */
export async function flushHeld(
  machineId: string,
  sessionId: string,
  send: (text: string) => Promise<string | null>,
): Promise<HeldMessage[]> {
  for (const item of heldFor(machineId, sessionId)) {
    let error: string | null;
    try {
      error = await send(item.text);
    } catch (failure) {
      error = failure instanceof Error ? failure.message : String(failure);
    }
    if (error) {
      const store = read();
      const key = storeKey(machineId, sessionId);
      store[key] = (store[key] ?? []).map((held) => (held.id === item.id ? { ...held, error } : held));
      write(store);
      break;
    }
    removeHeld(machineId, sessionId, item.id);
  }
  return heldFor(machineId, sessionId);
}
