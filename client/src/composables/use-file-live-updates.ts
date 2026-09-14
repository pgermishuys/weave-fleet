import { onScopeDispose, toValue, watch, type MaybeRefOrGetter } from "vue";
import { useEventListener } from "@vueuse/core";
import { useWeaveSocket } from "@/composables/use-weave-socket";
import type { DomainEvent } from "@/lib/domain-events";
import { fileCanvasId, useCanvasesStore } from "@/stores/canvases";
import { useFileBuffersStore } from "@/stores/file-buffers";

/** The agent edits a file several times in one turn; wait for the burst to settle, as Changes does. */
export const FILE_EVENT_DEBOUNCE_MS = 500;

/**
 * Whether a path from a `files.changed` event is an open file. The agent's edits arrive as
 * absolute paths (OpenCode sends them that way), saves as session-relative ones.
 */
export function isSameFile(eventPath: string, openPath: string): boolean {
  const event = eventPath.replace(/\\/g, "/");
  return event === openPath || event.endsWith(`/${openPath}`);
}

/**
 * Keeps open files in step with the disk. An open file is read again when the agent changes it
 * (`files.changed`), when the agent stops working (shell edits such as `sed` emit no file event), when
 * its tab becomes active, and when the window gets focus back. The editor side decides what a
 * new version means: a clean buffer updates in place, a dirty one gets the conflict bar.
 */
export function useFileLiveUpdates(sessionId: MaybeRefOrGetter<string | null | undefined>): void {
  const buffers = useFileBuffersStore();
  const canvases = useCanvasesStore();
  const { subscribeV2 } = useWeaveSocket();

  const pending = new Set<string>();
  let timer: ReturnType<typeof setTimeout> | undefined;

  async function refresh(paths: readonly string[]): Promise<void> {
    const id = toValue(sessionId);
    if (!id || paths.length === 0) return;
    // Buffers exist only once the editor has loaded, so this import is already resolved.
    const { refreshBuffer } = await import("@/lib/code-editor/buffers");
    for (const path of paths) {
      const result = await refreshBuffer(id, path);
      if (result?.kind === "updated" || result?.kind === "conflict") canvases.markUpdated(fileCanvasId(path));
    }
  }

  function flush(): void {
    clearTimeout(timer);
    timer = undefined;
    const paths = [...pending];
    pending.clear();
    void refresh(paths);
  }

  function queue(paths: readonly string[], delay = FILE_EVENT_DEBOUNCE_MS): void {
    if (paths.length === 0) return;
    for (const path of paths) pending.add(path);
    clearTimeout(timer);
    timer = setTimeout(flush, delay);
  }

  function openPaths(): string[] {
    const id = toValue(sessionId);
    return id ? buffers.openPaths(id) : [];
  }

  watch(
    () => toValue(sessionId),
    (id, _previous, onCleanup) => {
      if (!id) return;
      const unsubscribe = subscribeV2(
        `session:${id}`,
        () => {},
        // The topic is this session's, so the payload's session id isn't checked: relayed turn
        // events carry the harness's own id (OpenCode's `ses_…`), not Fleet's.
        (event: DomainEvent) => {
          if (event.type === "files.changed") {
            const changed = event.payload.files.map((file) => file.path);
            queue(openPaths().filter((path) => changed.some((eventPath) => isSameFile(eventPath, path))));
          } else if (event.type === "turn.ended" || event.type === "session.idled") {
            // The agent stopped: pooled OpenCode says so with session.idled, not turn.ended.
            queue(openPaths());
          }
        },
      );
      onCleanup(() => {
        unsubscribe();
        clearTimeout(timer);
        pending.clear();
      });
    },
    { immediate: true },
  );

  // A tab coming forward, or the window regaining focus, checks straight away.
  watch(
    () => {
      const id = toValue(sessionId);
      if (!id) return null;
      const state = canvases.sessionCanvases(id);
      return state.canvases.find((canvas) => canvas.id === state.activeId)?.file?.path ?? null;
    },
    (path) => {
      if (path && openPaths().includes(path)) queue([path], 0);
    },
  );

  useEventListener(window, "focus", () => queue(openPaths(), 0));

  onScopeDispose(() => clearTimeout(timer));
}
