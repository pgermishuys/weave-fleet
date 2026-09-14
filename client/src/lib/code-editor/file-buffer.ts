import { isolateHistory } from "@codemirror/commands";
import { EditorState, Transaction, type Extension, type TransactionSpec } from "@codemirror/state";
import type { FileBufferRecord, FileConflict } from "@/stores/file-buffers";
import { flashLines } from "./agent-lines";
import { minimalChange } from "./minimal-change";
import { readTextFormat, sameTextFormat, toDiskText, type TextFormat } from "./text-format";

/**
 * What happens to an open file's buffer when it's read, saved, or changed on disk. These work on
 * the store's record, through the mounted editor when there is one, so the view never goes stale.
 */

/** A file as the read endpoint returns it. */
export interface DiskFile {
  content: string | null;
  hash: string | null;
  isBinary: boolean;
  isTruncated: boolean;
}

export const TOO_LARGE_MESSAGE = "Too large to edit here (over 512 KB).";
export const NOT_TEXT_MESSAGE = "This file is binary or not UTF-8 text, so it can't be edited here.";

/** Why a file can't be edited here, or null when it can. */
export function unavailableReason(file: DiskFile): string | null {
  if (file.isTruncated) return TOO_LARGE_MESSAGE;
  if (file.isBinary || file.content === null || !file.hash) return NOT_TEXT_MESSAGE;
  return null;
}

export type BuildExtensions = (format: TextFormat) => Extension;

function newState(text: string, extensions: Extension): EditorState {
  return EditorState.create({ doc: text, extensions });
}

/** Apply a change through the mounted editor if there is one, so the view stays in step. */
export function commit(record: FileBufferRecord, spec: TransactionSpec): void {
  if (record.view) {
    record.view.dispatch(spec);
    record.state = record.view.state;
  } else if (record.state) {
    record.state = record.state.update(spec).state;
  }
}

function replaceState(record: FileBufferRecord, state: EditorState): void {
  record.state = state;
  record.view?.setState(state);
}

/** Fill a record from a disk read. Returns why it can't be edited, or null when it's ready. */
export function loadBuffer(record: FileBufferRecord, file: DiskFile, build: BuildExtensions): string | null {
  const reason = unavailableReason(file);
  if (reason) return reason;

  const { text, format } = readTextFormat(file.content ?? "");
  const state = newState(text, build(format));
  record.format = format;
  record.baseHash = file.hash;
  record.savedDoc = state.doc;
  replaceState(record, state);
  return null;
}

export function isDirty(record: FileBufferRecord): boolean {
  if (!record.state || !record.savedDoc) return false;
  return !record.state.doc.eq(record.savedDoc);
}

/** The bytes to save, as the text the server writes (a BOM goes back as U+FEFF). */
export function diskTextOf(record: FileBufferRecord): string {
  if (!record.state || !record.format) return "";
  return toDiskText(record.state, record.format);
}

export type DiskUpdate =
  | { kind: "unchanged" }
  | { kind: "updated"; lines: { from: number; to: number } | null }
  | { kind: "conflict"; conflict: FileConflict }
  | { kind: "unavailable"; message: string };

/**
 * The file on disk may have changed. The same hash changes nothing. A buffer without unsaved edits
 * takes the new text as one minimal edit, kept out of undo history, and its changed lines flash. A
 * buffer with unsaved edits is never touched: the caller shows the conflict bar instead.
 */
export function applyDiskFile(record: FileBufferRecord, file: DiskFile, build: BuildExtensions): DiskUpdate {
  if (file.hash && file.hash === record.baseHash) return { kind: "unchanged" };

  const reason = unavailableReason(file);
  if (isDirty(record)) {
    return { kind: "conflict", conflict: { diskText: reason ? null : file.content, hash: file.hash ?? "" } };
  }
  if (reason) return { kind: "unavailable", message: reason };

  const { text, format } = readTextFormat(file.content ?? "");
  if (!record.state || !record.format || !sameTextFormat(format, record.format)) {
    // A new line-break style or BOM can't be applied as an edit; start from a fresh state.
    const state = newState(text, build(format));
    record.format = format;
    record.baseHash = file.hash;
    record.savedDoc = state.doc;
    replaceState(record, state);
    return { kind: "updated", lines: null };
  }

  const next = record.state.toText(text);
  const change = minimalChange(record.state.doc, next);
  record.baseHash = file.hash;
  if (!change) {
    record.savedDoc = record.state.doc;
    return { kind: "unchanged" };
  }

  commit(record, {
    changes: { from: change.from, to: change.to, insert: change.insert },
    annotations: [Transaction.addToHistory.of(false), Transaction.remote.of(true)],
  });
  const lines: number[] = [];
  for (let line = change.lines.from; line <= change.lines.to; line++) lines.push(line);
  commit(record, { effects: flashLines(record.state!.doc, lines) });
  record.savedDoc = record.state!.doc;
  return { kind: "updated", lines: change.lines };
}

/** A save went through: the buffer now matches the file on disk. */
export function markSaved(record: FileBufferRecord, hash: string, savedDoc = record.state?.doc ?? null): void {
  record.baseHash = hash;
  record.savedDoc = savedDoc;
}

/**
 * "Use the agent's": the buffer takes the file on disk. It's one undoable edit, so Ctrl Z brings
 * your version back if you change your mind.
 */
export function takeDiskVersion(record: FileBufferRecord, conflict: FileConflict, build: BuildExtensions): void {
  if (conflict.diskText === null || !record.state) return;
  const { text, format } = readTextFormat(conflict.diskText);
  if (!record.format || !sameTextFormat(format, record.format)) {
    const state = newState(text, build(format));
    record.format = format;
    replaceState(record, state);
  } else {
    const next = record.state.toText(text);
    const change = minimalChange(record.state.doc, next);
    // Its own undo step, never merged with the typing before it.
    if (change) {
      commit(record, {
        changes: { from: change.from, to: change.to, insert: change.insert },
        annotations: isolateHistory.of("full"),
      });
    }
  }
  record.baseHash = conflict.hash;
  record.savedDoc = record.state!.doc;
}

/**
 * After Compare: your buffer, merged against the agent's version, is now based on it. A save
 * checks against the agent's hash, and the buffer stays unsaved until then.
 */
export function rebaseOnDisk(record: FileBufferRecord, conflict: FileConflict): void {
  if (conflict.diskText === null || !record.state) return;
  record.baseHash = conflict.hash;
  record.savedDoc = record.state.toText(readTextFormat(conflict.diskText).text);
}
