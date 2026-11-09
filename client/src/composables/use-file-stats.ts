import { shallowRef, watch, type ShallowRef } from "vue";
import { useMachineTarget } from "@/lib/machine-target";
import type { CapturedStats, FileStats } from "@/lib/turn-fold";

/**
 * The files the session changed and how much, from its diff (the numbers the Changes panel shows), for calls that
 * wrote a whole file and so can't say what they changed. It's read when `wanted` turns true and again whenever
 * `refreshOn` changes while wanted; null until known, and when the session's folder has no diff.
 */
export function useFileStats(options: {
  sessionId: () => string;
  wanted: () => boolean;
  refreshOn?: () => unknown;
}): ShallowRef<FileStats | null> {
  const machine = useMachineTarget();
  const stats = shallowRef<FileStats | null>(null);
  let request = 0;

  async function load(): Promise<void> {
    const id = options.sessionId();
    const mine = ++request;
    try {
      const { data, response } = await machine.api.GET("/api/sessions/{id}/diffs", { params: { path: { id } } });
      if (mine !== request || !response.ok) return;
      const body = data as unknown as { diffs?: { file: string; additions: number; deletions: number; status?: "added" | "deleted" | "modified" }[]; available?: boolean } | undefined;
      stats.value = body?.available === false || !Array.isArray(body?.diffs)
        ? null
        : new Map(body.diffs.map((diff) => [diff.file, { additions: diff.additions, deletions: diff.deletions, status: diff.status }]));
    } catch {
      // The counts stay unknown (not drawn) rather than wrong.
    }
  }

  watch(options.sessionId, () => {
    request += 1;
    stats.value = null;
  });
  watch(
    [options.sessionId, options.wanted, () => options.refreshOn?.()],
    ([, wanted]) => {
      if (wanted) void load();
    },
    { immediate: true },
  );
  return stats;
}

const KEY = "fleet.turn-stats.";
const MAX_KEPT = 500;

function readKept(sessionId: string): Map<string, { additions: number; deletions: number }> {
  try {
    const raw = localStorage.getItem(KEY + sessionId);
    return new Map(raw ? (JSON.parse(raw) as [string, { additions: number; deletions: number }][]) : []);
  } catch {
    return new Map();
  }
}

/**
 * The change each whole-file write made, kept as its turn ended (call id → counts), and kept across reloads. A past
 * turn's card reads from here; what was never kept is unknown and draws no counts, never today's diff.
 */
export function useCapturedStats(sessionId: () => string): { captured: ShallowRef<CapturedStats>; keep: (entries: [string, { additions: number; deletions: number }][]) => void } {
  const captured = shallowRef<CapturedStats>(readKept(sessionId()));
  watch(sessionId, (id) => {
    captured.value = readKept(id);
  });
  function keep(entries: [string, { additions: number; deletions: number }][]): void {
    const fresh = entries.filter(([id]) => !captured.value.has(id));
    if (fresh.length === 0) return;
    const next = new Map([...captured.value, ...fresh]);
    while (next.size > MAX_KEPT) next.delete(next.keys().next().value as string);
    captured.value = next;
    try {
      localStorage.setItem(KEY + sessionId(), JSON.stringify([...next]));
    } catch {
      // Not kept across a reload; it still holds for this page.
    }
  }
  return { captured, keep };
}
