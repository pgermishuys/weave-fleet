import { shallowRef, watch, type ShallowRef } from "vue";
import { useMachineTarget } from "@/lib/machine-target";
import type { FileStats, TurnStatsSnapshot } from "@/lib/turn-fold";

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

/**
 * What the session's files were as each turn ended, from the server (it takes the snapshot as the turn ends, so every
 * browser, phone and machine reads the same). Read when `wanted` turns true and again whenever `refreshOn` changes
 * while wanted; empty until known.
 */
export function useTurnStats(options: {
  sessionId: () => string;
  wanted: () => boolean;
  refreshOn?: () => unknown;
}): ShallowRef<readonly TurnStatsSnapshot[]> {
  const machine = useMachineTarget();
  const snapshots = shallowRef<readonly TurnStatsSnapshot[]>([]);
  let request = 0;

  async function load(): Promise<void> {
    const id = options.sessionId();
    const mine = ++request;
    try {
      const { data, response } = await machine.api.GET("/api/sessions/{id}/turn-stats", { params: { path: { id } } });
      if (mine !== request || !response.ok) return;
      const body = data as unknown as { snapshots?: { endedAt: string; files: { file: string; additions: number; deletions: number }[] }[] } | undefined;
      snapshots.value = (body?.snapshots ?? []).map((snapshot) => ({
        endedAt: Date.parse(snapshot.endedAt),
        files: new Map(snapshot.files.map((file) => [file.file, { additions: file.additions, deletions: file.deletions }])),
      }));
    } catch {
      // The counts stay unknown (not drawn) rather than wrong.
    }
  }

  watch(options.sessionId, () => {
    request += 1;
    snapshots.value = [];
  });
  watch(
    [options.sessionId, options.wanted, () => options.refreshOn?.()],
    ([, wanted]) => {
      if (wanted) void load();
    },
    { immediate: true },
  );
  return snapshots;
}
