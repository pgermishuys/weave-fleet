/**
 * When you last looked at a session on the phone, so coming back shows "Since you looked at 14:20" before what's new.
 * Kept per machine and session in localStorage. No Vue.
 */
import type { PhoneBlock, PhoneItem } from "@/lib/phone/fold-steps";

const KEY = "weave:phone-last-seen";
const MAX_ENTRIES = 200;

function read(): Record<string, number> {
  try {
    const parsed: unknown = JSON.parse(localStorage.getItem(KEY) ?? "{}");
    return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? parsed as Record<string, number> : {};
  } catch {
    return {};
  }
}

export function lastSeenAt(machineId: string, sessionId: string): number | null {
  const value = read()[`${machineId}:${sessionId}`];
  return typeof value === "number" ? value : null;
}

export function markSeen(machineId: string, sessionId: string, at: number): void {
  const entries = read();
  entries[`${machineId}:${sessionId}`] = at;
  const trimmed = Object.entries(entries).sort((a, b) => b[1] - a[1]).slice(0, MAX_ENTRIES);
  try {
    localStorage.setItem(KEY, JSON.stringify(Object.fromEntries(trimmed)));
  } catch {
    // Without storage there's no marker next time; nothing else breaks.
  }
}

/**
 * Where the "Since you looked" line goes: before the first block from the agent (or you) that's newer than when you
 * last looked. Null when you've never looked, or nothing's new. Blocks without a time count as old.
 */
export function sinceYouLookedIndex(blocks: readonly (PhoneBlock | PhoneItem)[], seenAt: number | null): number | null {
  if (seenAt === null) return null;
  const index = blocks.findIndex((block) => "createdAt" in block && typeof block.createdAt === "number" && block.createdAt > seenAt);
  return index > 0 ? index : null;
}
