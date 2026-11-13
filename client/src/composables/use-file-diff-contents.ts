import { shallowRef, type ShallowRef } from "vue";
import type { FileDiffItem } from "@/api/client";
import { apiFetchOn } from "@/lib/api-client";
import type { MachineTarget } from "@/lib/machine-target";

export type FileDiffEntry =
  | { status: "loading" }
  | { status: "ready"; item: FileDiffItem }
  | { status: "error"; message: string };

/**
 * What was fetched, kept by machine and session so a panel that closes and reopens (or a session switched away from and
 * back to) draws the files it already had at once; each is still fetched again quietly and replaced when it differs.
 */
const MAX_CACHED = 200;
const cache = new Map<string, FileDiffItem>();

function cacheKey(machine: MachineTarget, sessionId: string, path: string): string {
  return `${machine.key}\u0000${sessionId}\u0000${path}`;
}

function remember(key: string, item: FileDiffItem): void {
  cache.delete(key);
  cache.set(key, item);
  if (cache.size > MAX_CACHED) cache.delete(cache.keys().next().value as string);
}

/**
 * At most this many file diffs are asked of one machine at a time. A browser opens six connections to a server, and
 * a long list (find loads every file) fired at once held up everything else the app asked for (switching session,
 * sending a message) until the last diff came back.
 */
const MAX_IN_FLIGHT = 4;
const inFlight = new Map<string, number>();
const queued = new Map<string, Array<() => void>>();

async function withSlot<T>(machineKey: string, run: () => Promise<T>): Promise<T> {
  if ((inFlight.get(machineKey) ?? 0) >= MAX_IN_FLIGHT) {
    // Woken with the slot of a fetch that finished: the count stays as it is.
    await new Promise<void>((resolve) => {
      const queue = queued.get(machineKey) ?? [];
      queue.push(resolve);
      queued.set(machineKey, queue);
    });
  } else {
    inFlight.set(machineKey, (inFlight.get(machineKey) ?? 0) + 1);
  }
  try {
    return await run();
  } finally {
    const next = queued.get(machineKey)?.shift();
    if (next) next();
    else inFlight.set(machineKey, (inFlight.get(machineKey) ?? 1) - 1);
  }
}

/**
 * The contents (before and after) of the files Changes shows, fetched one file at a time from /diffs/file as each
 * section opens. An entry is replaced when its file's counts change (the agent edited it again) or on `reload`.
 */
export function useFileDiffContents(
  machine: MachineTarget,
  sessionId: () => string,
): {
  entries: Readonly<ShallowRef<Readonly<Record<string, FileDiffEntry>>>>;
  load: (path: string, version: string) => void;
  reload: (path: string, version: string) => void;
} {
  const entries = shallowRef<Record<string, FileDiffEntry>>({});
  const versions = new Map<string, string>();
  let reloads = 0;

  function set(path: string, entry: FileDiffEntry): void {
    entries.value = { ...entries.value, [path]: entry };
  }

  async function fetchOne(path: string, version: string): Promise<void> {
    const id = sessionId();
    versions.set(path, version);
    if (entries.value[path]?.status !== "ready") {
      const cached = cache.get(cacheKey(machine, id, path));
      set(path, cached ? { status: "ready", item: cached } : { status: "loading" });
    }
    try {
      const response = await withSlot(machine.key, async () =>
        // Replaced while it waited (the agent edited the file again): the newer request asks instead.
        versions.get(path) !== version
          ? null
          : apiFetchOn(machine.connection, `/api/sessions/${encodeURIComponent(id)}/diffs/file?path=${encodeURIComponent(path)}`),
      );
      if (!response || versions.get(path) !== version) return;
      if (!response.ok) {
        set(path, { status: "error", message: `HTTP ${response.status}` });
        return;
      }
      const item = (await response.json()) as FileDiffItem;
      remember(cacheKey(machine, id, path), item);
      set(path, { status: "ready", item });
    } catch (error) {
      if (versions.get(path) !== version) return;
      set(path, { status: "error", message: error instanceof Error ? error.message : String(error) });
    }
  }

  function load(path: string, version: string): void {
    if (versions.get(path) === version && entries.value[path]) return;
    void fetchOne(path, version);
  }

  function reload(path: string, version: string): void {
    void fetchOne(path, `${version}#${++reloads}`);
  }

  return { entries, load, reload };
}
