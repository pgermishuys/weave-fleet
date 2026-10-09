import { defineStore } from "pinia";
import { reactive } from "vue";
import { resolveSessionFiles } from "@/api/session-files";
import type { MachineTarget } from "@/lib/machine-target";

/** The most paths one request asks about; the server looks at no more. */
const MAX_PATHS_PER_REQUEST = 200;

function key(sessionId: string, path: string): string {
  return `${sessionId}\n${path}`;
}

/**
 * Which paths named in a session's messages are files in its folder, so only those show as links. Each path is asked
 * about once, with everything a render names in one request. Paths that weren't files are asked about again after a
 * turn, when the agent may have made them.
 */
export const useFileLinksStore = defineStore("file-links", () => {
  /** The path from the session's folder, or null when it isn't a file there. */
  const known = reactive(new Map<string, string | null>());
  const asking = new Set<string>();
  const queued = new Map<string, { machine: MachineTarget; paths: string[] }>();

  /** The file `path` names in the session's folder; null when it names none, undefined until the server has said. */
  function resolve(sessionId: string, path: string): string | null | undefined {
    return known.get(key(sessionId, path));
  }

  async function flush(sessionId: string): Promise<void> {
    const batch = queued.get(sessionId);
    queued.delete(sessionId);
    if (!batch) return;

    for (let start = 0; start < batch.paths.length; start += MAX_PATHS_PER_REQUEST) {
      const paths = batch.paths.slice(start, start + MAX_PATHS_PER_REQUEST);
      try {
        const files = await resolveSessionFiles(batch.machine, sessionId, paths);
        for (const path of paths) known.set(key(sessionId, path), files.get(path) ?? null);
      } catch {
        // Not known yet, so the next render that names them asks again.
      } finally {
        for (const path of paths) asking.delete(key(sessionId, path));
      }
    }
  }

  /** Ask about the paths not known yet. Calls in the same tick share one request. */
  function request(machine: MachineTarget, sessionId: string, paths: readonly string[]): void {
    for (const path of paths) {
      if (known.has(key(sessionId, path)) || asking.has(key(sessionId, path))) continue;

      let batch = queued.get(sessionId);
      if (!batch) {
        batch = { machine, paths: [] };
        queued.set(sessionId, batch);
        queueMicrotask(() => void flush(sessionId));
      }
      asking.add(key(sessionId, path));
      batch.paths.push(path);
    }
  }

  /** Forget the paths that weren't files, so they're asked about again. Files found stay links. */
  function forgetMissing(sessionId: string): void {
    const prefix = key(sessionId, "");
    for (const [entry, path] of known) {
      if (path === null && entry.startsWith(prefix)) known.delete(entry);
    }
  }

  return { resolve, request, forgetMissing };
});
