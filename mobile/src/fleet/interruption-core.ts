// Sessions a Fleet restart cut off: reading the mark off the wire, listing them, and the words for the list's row.
import type { SessionListItem } from "@fleet/api/client";

/** As the server sends `interrupted` (client/src/lib/session-interruption.ts, plus `auto`: Fleet is continuing it by itself). */
export interface Interruption {
  at: string;
  waiting: boolean;
  lastCall: string | null;
  changedFiles: number | null;
  auto: boolean;
}

export function toInterruption(value: unknown): Interruption | null {
  if (!value || typeof value !== "object") return null;
  const { at, waiting, lastCall, changedFiles, auto } = value as Record<string, unknown>;
  if (typeof at !== "string" || !Number.isFinite(Date.parse(at))) return null;
  return {
    at,
    waiting: waiting === true,
    lastCall: typeof lastCall === "string" && lastCall.trim() ? lastCall : null,
    changedFiles: typeof changedFiles === "number" && Number.isFinite(changedFiles) ? changedFiles : null,
    auto: auto === true,
  };
}

export const interruptionOf = (item: SessionListItem): Interruption | null => toInterruption((item as { interrupted?: unknown }).interrupted);

export interface InterruptedEntry {
  key: string;
  machineId: string;
  machineName: string;
  item: SessionListItem;
  interruption: Interruption;
}

/** Every unarchived session (sub-agents too) with a mark, oldest cut first. */
export function interruptedEntries(lists: ReadonlyArray<{ machineId: string; machineName: string; items: readonly SessionListItem[] }>): InterruptedEntry[] {
  const entries: InterruptedEntry[] = [];
  for (const { machineId, machineName, items } of lists) {
    for (const item of items) {
      const interruption = interruptionOf(item);
      if (interruption && !item.archivedAt) entries.push({ key: `${machineId}:${item.session.id}`, machineId, machineName, item, interruption });
    }
  }
  return entries.sort((a, b) => Date.parse(a.interruption.at) - Date.parse(b.interruption.at));
}

const sessions = (n: number) => `${n} ${n === 1 ? "session" : "sessions"}`;

/** The row at the top of the list; `automatic` when Fleet is continuing some, which brings "Stop all". */
export function interruptionBanner(entries: readonly InterruptedEntry[]): { text: string; automatic: boolean } | null {
  if (entries.length === 0) return null;
  const auto = entries.filter((e) => e.interruption.auto).length;
  if (auto > 0) return { text: `Fleet restarted. Continuing ${sessions(auto)}.`, automatic: true };
  return { text: `Fleet restarted. ${entries.length === 1 ? "1 session was" : `${entries.length} sessions were`} cut off.`, automatic: false };
}
