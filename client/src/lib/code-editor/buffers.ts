import type { Extension } from "@codemirror/state";
import { api } from "@/api/client";
import { readSessionFile, writeSessionFile } from "@/api/session-files";
import { useFileBuffersStore, type FileBufferRecord } from "@/stores/file-buffers";
import type { ListFolder } from "./completion";
import { bufferExtensions, languageSlot } from "./editor-extensions";
import {
  applyDiskFile,
  commit,
  diskTextOf,
  isDirty,
  loadBuffer,
  markSaved,
  rebaseOnDisk,
  takeDiskVersion,
  type BuildExtensions,
  type DiskFile,
  type DiskUpdate,
} from "./file-buffer";
import { loadLanguage } from "./languages";

/**
 * Opening, re-reading and saving files in the editor: the file-buffer transitions plus the API and
 * the buffers store. Part of the editor's lazy chunk.
 */

type Store = ReturnType<typeof useFileBuffersStore>;

const languages = new WeakMap<FileBufferRecord, Extension>();
const refreshing = new WeakMap<FileBufferRecord, Promise<DiskUpdate | null>>();

// ─── Folder listings for import completion ──────────────────────────────────

const FOLDER_CACHE_MS = 10_000;
const folderCache = new Map<string, { at: number; entries: Promise<readonly string[]> }>();

/** Lists a folder of the session's files (what git would list), cached briefly. */
export function folderLister(sessionId: string): ListFolder {
  return (folder) => {
    const key = `${sessionId}\u0000${folder}`;
    const cached = folderCache.get(key);
    if (cached && Date.now() - cached.at < FOLDER_CACHE_MS) return cached.entries;

    const entries = api
      .GET("/api/sessions/{id}/find/files", { params: { path: { id: sessionId }, query: { q: folder } } })
      .then(({ data, response }) => {
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        const files = (data as unknown as { files?: string[] } | undefined)?.files;
        return Array.isArray(files) ? files : [];
      });
    entries.catch(() => folderCache.delete(key));
    folderCache.set(key, { at: Date.now(), entries });
    return entries;
  };
}

// ─── Buffers ─────────────────────────────────────────────────────────────────

function builder(store: Store, record: FileBufferRecord): BuildExtensions {
  return (format) =>
    bufferExtensions({
      path: record.path,
      format,
      language: languages.get(record) ?? null,
      listFolder: folderLister(record.sessionId),
      onSave: () => record.handlers.save?.(),
      onUpdate: (update) => {
        record.state = update.state;
        if (update.docChanged) store.patch(record.sessionId, record.path, { dirty: isDirty(record) });
        record.handlers.update?.(update);
      },
    });
}

async function applyLanguage(record: FileBufferRecord): Promise<void> {
  const support = await loadLanguage(record.path);
  if (!support || !record.state) return;
  languages.set(record, support);
  commit(record, { effects: languageSlot.reconfigure(support) });
}

function toDiskFile(response: Awaited<ReturnType<typeof readSessionFile>>): DiskFile {
  return {
    content: response.content ?? null,
    hash: response.hash ?? null,
    isBinary: response.isBinary,
    isTruncated: response.isTruncated,
  };
}

/** Open a file's buffer, reading it the first time. Safe to call again; it won't read twice. */
export async function openBuffer(sessionId: string, path: string): Promise<FileBufferRecord> {
  const store = useFileBuffersStore();
  const record = store.ensure(sessionId, path);
  const status = store.info(sessionId, path)?.status;
  if (record.state || status === "unavailable") return record;

  const pending = refreshing.get(record);
  if (pending) {
    await pending;
    return record;
  }

  const load = (async (): Promise<DiskUpdate | null> => {
    store.patch(sessionId, path, { status: "loading", message: null });
    try {
      const file = toDiskFile(await readSessionFile(sessionId, path));
      const reason = loadBuffer(record, file, builder(store, record));
      store.patch(sessionId, path, reason
        ? { status: "unavailable", message: reason }
        : { status: "ready", message: null, dirty: false, conflict: null });
      if (!reason) void applyLanguage(record);
    } catch (error) {
      store.patch(sessionId, path, {
        status: "error",
        message: error instanceof Error && error.message.includes("404")
          ? "This file isn't in the session's folder any more."
          : "The file couldn't be read.",
      });
    }
    return null;
  })();

  refreshing.set(record, load);
  try {
    await load;
  } finally {
    refreshing.delete(record);
  }
  return record;
}

/**
 * Read an open file again and apply it: a clean buffer updates in place, a dirty one gets the
 * conflict bar. Skipped while a save is in flight (that save's own `files.changed` would otherwise
 * look like the agent's change). Overlapping calls share one read.
 */
export async function refreshBuffer(sessionId: string, path: string): Promise<DiskUpdate | null> {
  const store = useFileBuffersStore();
  const record = store.record(sessionId, path);
  const info = store.info(sessionId, path);
  if (!record || !info || info.saving) return null;
  if (info.status !== "ready" && info.status !== "unavailable") return null;

  const pending = refreshing.get(record);
  if (pending) return pending;

  const run = (async (): Promise<DiskUpdate | null> => {
    let file: DiskFile;
    try {
      file = toDiskFile(await readSessionFile(sessionId, path));
    } catch {
      return null;
    }
    if (store.info(sessionId, path)?.saving) return null;

    if (info.status === "unavailable") {
      const reason = loadBuffer(record, file, builder(store, record));
      store.patch(sessionId, path, reason ? { message: reason } : { status: "ready", message: null, dirty: false });
      if (!reason) void applyLanguage(record);
      return reason ? { kind: "unavailable", message: reason } : { kind: "updated", lines: null };
    }

    const result = applyDiskFile(record, file, builder(store, record));
    switch (result.kind) {
      case "updated":
        store.patch(sessionId, path, { dirty: false, conflict: null });
        break;
      case "conflict": {
        const current = store.info(sessionId, path)?.conflict;
        if (current?.hash !== result.conflict.hash) store.patch(sessionId, path, { conflict: result.conflict });
        break;
      }
      case "unavailable":
        store.patch(sessionId, path, { status: "unavailable", message: result.message, dirty: false, conflict: null });
        break;
      case "unchanged":
        break;
    }
    return result;
  })();

  refreshing.set(record, run);
  try {
    return await run;
  } finally {
    refreshing.delete(record);
  }
}

export type SaveOutcome =
  | { kind: "saved" }
  | { kind: "conflict" }
  | { kind: "error"; message: string }
  | { kind: "skipped" };

/**
 * Save the buffer. Normally the save carries the hash the buffer is based on; `overwrite` ("Keep
 * mine") carries the conflicting version's hash instead, so it replaces exactly the file that was
 * shown, and still loses to a newer change.
 */
export async function saveBuffer(sessionId: string, path: string, options: { overwrite?: boolean } = {}): Promise<SaveOutcome> {
  const store = useFileBuffersStore();
  const record = store.record(sessionId, path);
  const info = store.info(sessionId, path);
  if (!record?.state || !info || info.status !== "ready" || info.saving) return { kind: "skipped" };

  const baseHash = options.overwrite && info.conflict ? info.conflict.hash : record.baseHash;
  if (!baseHash) return { kind: "skipped" };

  const sentDoc = record.state.doc;
  const content = diskTextOf(record);
  store.patch(sessionId, path, { saving: true });
  try {
    const result = await writeSessionFile(sessionId, path, content, baseHash);
    if (result.saved) {
      markSaved(record, result.hash, sentDoc);
      store.patch(sessionId, path, { saving: false, conflict: null, dirty: isDirty(record) });
      return { kind: "saved" };
    }
    store.patch(sessionId, path, { saving: false, conflict: { diskText: result.content, hash: result.hash } });
    return { kind: "conflict" };
  } catch (error) {
    store.patch(sessionId, path, { saving: false });
    return { kind: "error", message: error instanceof Error ? error.message : "The file couldn't be saved." };
  }
}

/** "Use the agent's": replace the buffer with the file on disk. */
export function useDiskVersion(sessionId: string, path: string): void {
  const store = useFileBuffersStore();
  const record = store.record(sessionId, path);
  const conflict = store.info(sessionId, path)?.conflict;
  if (!record || !conflict) return;
  takeDiskVersion(record, conflict, builder(store, record));
  store.patch(sessionId, path, { conflict: null, dirty: isDirty(record) });
}

/** Compare is done: the buffer (merged by hand) is now based on the agent's version. */
export function finishCompare(sessionId: string, path: string): void {
  const store = useFileBuffersStore();
  const record = store.record(sessionId, path);
  const conflict = store.info(sessionId, path)?.conflict;
  if (!record || !conflict) return;
  rebaseOnDisk(record, conflict);
  store.patch(sessionId, path, { conflict: null, dirty: isDirty(record) });
}

export function closeBuffer(sessionId: string, path: string): void {
  useFileBuffersStore().remove(sessionId, path);
}
