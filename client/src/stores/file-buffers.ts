import { defineStore } from "pinia";
import { computed, markRaw, shallowRef } from "vue";
import type { EditorState, Text } from "@codemirror/state";
import type { EditorView, ViewUpdate } from "@codemirror/view";
import type { TextFormat } from "@/lib/code-editor/text-format";

/**
 * Open files' buffers, per session and path. They live here rather than in the file canvas because
 * the canvas host keeps only 8 tabs alive (`KeepAlive :max="8"`): the 9th would take the unsaved
 * text and undo history of the first with it. The beforeunload guard reads this store too.
 *
 * Only types come from CodeMirror here, so the store stays out of the editor's lazy chunk. The
 * editor side (`lib/code-editor/buffers.ts`) creates and changes the states.
 */

/** The file changed on disk while the buffer had unsaved edits, or a save lost the race. */
export interface FileConflict {
  /** What's on disk now (the agent's version); null when it's no longer text. */
  diskText: string | null;
  hash: string;
}

export type FileBufferStatus = "loading" | "ready" | "unavailable" | "error";

/** What the tab and bar show. Reactive; changes rarely (not on every keystroke). */
export interface FileBufferInfo {
  status: FileBufferStatus;
  /** Why the file can't be edited here, or why it failed to load. */
  message: string | null;
  dirty: boolean;
  conflict: FileConflict | null;
  saving: boolean;
}

/** Hooks a mounted file canvas sets; the editor's keymap and update listener call them. */
export interface FileBufferHandlers {
  save?: () => void;
  update?: (update: ViewUpdate) => void;
}

/** The editor side of a buffer. Never reactive: the state changes on every keystroke. */
export interface FileBufferRecord {
  readonly sessionId: string;
  readonly path: string;
  state: EditorState | null;
  /** The document as last read from or saved to disk; the buffer is dirty when it differs. */
  savedDoc: Text | null;
  /** Hash of the disk version the buffer is based on; a save sends it. */
  baseHash: string | null;
  format: TextFormat | null;
  /** The mounted editor, when the tab is showing. */
  view: EditorView | null;
  handlers: FileBufferHandlers;
}

const IDLE_INFO: FileBufferInfo = { status: "loading", message: null, dirty: false, conflict: null, saving: false };

export function fileBufferKey(sessionId: string, path: string): string {
  return `${sessionId}\u0000${path}`;
}

export const useFileBuffersStore = defineStore("file-buffers", () => {
  const infos = shallowRef<Record<string, FileBufferInfo>>({});
  const records = new Map<string, FileBufferRecord>();

  function info(sessionId: string, path: string): FileBufferInfo | undefined {
    return infos.value[fileBufferKey(sessionId, path)];
  }

  function record(sessionId: string, path: string): FileBufferRecord | undefined {
    return records.get(fileBufferKey(sessionId, path));
  }

  /** The buffer for a file, created (loading) if it isn't open yet. */
  function ensure(sessionId: string, path: string): FileBufferRecord {
    const key = fileBufferKey(sessionId, path);
    let existing = records.get(key);
    if (!existing) {
      existing = markRaw<FileBufferRecord>({
        sessionId,
        path,
        state: null,
        savedDoc: null,
        baseHash: null,
        format: null,
        view: null,
        handlers: {},
      });
      records.set(key, existing);
      infos.value = { ...infos.value, [key]: { ...IDLE_INFO } };
    }
    return existing;
  }

  function patch(sessionId: string, path: string, next: Partial<FileBufferInfo>): void {
    const key = fileBufferKey(sessionId, path);
    const current = infos.value[key];
    if (!current) return;
    const changed = (Object.keys(next) as (keyof FileBufferInfo)[]).some((name) => current[name] !== next[name]);
    if (!changed) return;
    infos.value = { ...infos.value, [key]: { ...current, ...next } };
  }

  function remove(sessionId: string, path: string): void {
    const key = fileBufferKey(sessionId, path);
    const existing = records.get(key);
    existing?.view?.destroy();
    records.delete(key);
    if (!(key in infos.value)) return;
    const rest = { ...infos.value };
    delete rest[key];
    infos.value = rest;
  }

  function isDirty(sessionId: string, path: string): boolean {
    return info(sessionId, path)?.dirty ?? false;
  }

  /** Paths open in a session, for re-checking them when the agent changes files. */
  function openPaths(sessionId: string): string[] {
    return [...records.values()].filter((entry) => entry.sessionId === sessionId).map((entry) => entry.path);
  }

  /** Files with unsaved changes, across sessions. */
  const unsaved = computed(() =>
    Object.entries(infos.value)
      .filter(([, value]) => value.dirty)
      .map(([key]) => {
        const [sessionId, path] = key.split("\u0000");
        return { sessionId, path };
      }),
  );

  return { infos, info, record, ensure, patch, remove, isDirty, openPaths, unsaved };
});
