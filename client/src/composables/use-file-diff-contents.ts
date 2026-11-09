import { shallowRef, type ShallowRef } from "vue";
import type { FileDiffItem } from "@/api/client";
import { apiFetchOn } from "@/lib/api-client";
import type { MachineTarget } from "@/lib/machine-target";

export type FileDiffEntry =
  | { status: "loading" }
  | { status: "ready"; item: FileDiffItem }
  | { status: "error"; message: string };

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
    if (entries.value[path]?.status !== "ready") set(path, { status: "loading" });
    try {
      const response = await apiFetchOn(
        machine.connection,
        `/api/sessions/${encodeURIComponent(id)}/diffs/file?path=${encodeURIComponent(path)}`,
      );
      if (versions.get(path) !== version) return;
      if (!response.ok) {
        set(path, { status: "error", message: `HTTP ${response.status}` });
        return;
      }
      set(path, { status: "ready", item: (await response.json()) as FileDiffItem });
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
