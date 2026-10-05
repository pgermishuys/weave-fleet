/**
 * `@` references in a draft: `@src/app.ts`, `@src/components/`, `@shuttle`, and sessions picked from the `@` list
 * (`@t3code-notes`, see `session-references.ts`). They're plain text in the message (the agent reads the path); the
 * composer only draws them differently, and edits the ones picked from the `@` list as one piece: Backspace and
 * Delete take the whole reference out, and the caret steps over it.
 */
import { reactive } from "vue";
import { readStoredDraft, writeStoredDraft } from "@/lib/draft-storage";
import { textHasToken } from "@/lib/session-references";

export interface DraftSegment {
  text: string;
  reference?: "file" | "folder" | "session";
  /** Picked from the `@` list: drawn as a reference even with the caret at its end, and edited as one piece. */
  picked?: boolean;
}

/** Where a picked reference is in the draft: `start` is the `@`, `end` is just past its last character. */
export interface ReferenceRange {
  start: number;
  end: number;
}

// "@" at the start or after whitespace, then everything up to the next whitespace.
const REFERENCE_PATTERN = /(^|\s)(@[^\s@][^\s]*)/g;
// Sentence punctuation typed straight after a reference isn't part of it.
const TRAILING_PUNCTUATION = /[.,;:!?)\]}'"]+$/;

/**
 * Splits a draft into plain text and references. A token the caret is in (or at the end of) is
 * still being typed, so it stays plain until you move past it, unless it was picked from the `@`
 * list: sessions in `sessionTokens`, files and folders in `fileTokens`.
 */
export function splitDraftReferences(
  text: string,
  caret: number | null = null,
  sessionTokens: ReadonlySet<string> = new Set(),
  fileTokens: ReadonlySet<string> = new Set(),
): DraftSegment[] {
  const segments: DraftSegment[] = [];
  let plainStart = 0;

  for (const match of text.matchAll(REFERENCE_PATTERN)) {
    const token = match[2].replace(TRAILING_PUNCTUATION, "");
    const start = (match.index ?? 0) + match[1].length;
    const end = start + token.length;
    const tokenEnd = start + match[2].length;

    const isSession = sessionTokens.has(token);
    const picked = isSession || fileTokens.has(token);

    if (token.length < 2 || (!picked && caret !== null && caret > start && caret <= tokenEnd)) {
      continue;
    }

    if (start > plainStart) {
      segments.push({ text: text.slice(plainStart, start) });
    }
    segments.push({
      text: token,
      reference: isSession ? "session" : token.endsWith("/") ? "folder" : "file",
      ...(picked ? { picked } : {}),
    });
    plainStart = end;
  }

  if (plainStart < text.length) {
    segments.push({ text: text.slice(plainStart) });
  }

  return segments;
}

/** Where the references picked from the `@` list are in `text`, in order. */
export function pickedReferenceRanges(
  text: string,
  sessionTokens: ReadonlySet<string>,
  fileTokens: ReadonlySet<string>,
): ReferenceRange[] {
  const ranges: ReferenceRange[] = [];
  let offset = 0;
  for (const segment of splitDraftReferences(text, null, sessionTokens, fileTokens)) {
    if (segment.picked) {
      ranges.push({ start: offset, end: offset + segment.text.length });
    }
    offset += segment.text.length;
  }
  return ranges;
}

export interface ReferenceKeyAction {
  /** The selection to make before the key does its work. */
  selectionStart: number;
  selectionEnd: number;
  /** Whether the key's own work is stopped: the arrows are, Backspace and Delete then delete the selection. */
  preventDefault: boolean;
}

/**
 * What a key does to a picked reference at a caret (no selection). Backspace after it, or Delete before it, selects
 * the whole reference and lets the browser delete the selection, so it goes in one piece and Undo puts it back. The
 * left and right arrows step over it. Null when the key doesn't touch a picked reference.
 */
export function referenceKeyAction(key: string, caret: number, ranges: readonly ReferenceRange[]): ReferenceKeyAction | null {
  for (const { start, end } of ranges) {
    const touchesEnd = caret > start && caret <= end;
    const touchesStart = caret >= start && caret < end;
    if (key === "Backspace" && touchesEnd) return { selectionStart: start, selectionEnd: end, preventDefault: false };
    if (key === "Delete" && touchesStart) return { selectionStart: start, selectionEnd: end, preventDefault: false };
    if (key === "ArrowLeft" && touchesEnd) return { selectionStart: start, selectionEnd: start, preventDefault: true };
    if (key === "ArrowRight" && touchesStart) return { selectionStart: end, selectionEnd: end, preventDefault: true };
  }
  return null;
}

/** A caret inside a picked reference (after a click, say) moves to its nearer edge; anywhere else it stays. */
export function caretOutsideReferences(caret: number, ranges: readonly ReferenceRange[]): number {
  const inside = ranges.find(({ start, end }) => caret > start && caret < end);
  if (!inside) return caret;
  return caret - inside.start <= inside.end - caret ? inside.start : inside.end;
}

// ── The files and folders each composer picked ─────────────────────────────

/**
 * Each composer's picked files and folders (with the `@`), by the session the composer sends to. Kept in the browser
 * with the draft, and only while they're still in it: once a reference is deleted, typing it again is plain typing.
 */
const pickedFiles = reactive<Record<string, string[]>>({});

const fileStorageKey = (draftSessionId: string) => `file-refs.${draftSessionId}`;

function filePicksFor(draftSessionId: string): string[] {
  let picks = pickedFiles[draftSessionId];
  if (!picks) {
    let stored: unknown = [];
    try {
      stored = JSON.parse(readStoredDraft(fileStorageKey(draftSessionId)) ?? "[]");
    } catch {
      stored = [];
    }
    pickedFiles[draftSessionId] = Array.isArray(stored) ? stored.filter((token) => typeof token === "string") : [];
    picks = pickedFiles[draftSessionId];
  }
  return picks;
}

function storeFilePicks(draftSessionId: string, picks: string[]): void {
  pickedFiles[draftSessionId] = picks;
  writeStoredDraft(fileStorageKey(draftSessionId), picks.length > 0 ? JSON.stringify(picks) : "");
}

/** Remembers that `token` (`@src/app.ts`, `@src/components/`) was picked from this composer's `@` list. */
export function rememberFileReference(draftSessionId: string, token: string): void {
  const picks = filePicksFor(draftSessionId);
  if (!picks.includes(token)) {
    storeFilePicks(draftSessionId, [...picks, token]);
  }
}

/** Forgets the picked files and folders no longer in `text`. */
export function forgetFileReferencesNotIn(draftSessionId: string, text: string): void {
  const picks = filePicksFor(draftSessionId);
  const kept = picks.filter((token) => textHasToken(text, token));
  if (kept.length !== picks.length) {
    storeFilePicks(draftSessionId, kept);
  }
}

/** The files and folders this composer picked that are still in its draft (reactive). */
export function pickedFileTokens(draftSessionId: string): ReadonlySet<string> {
  return new Set(filePicksFor(draftSessionId));
}

/** For tests: forgets every composer's picked files. */
export function resetFileReferences(): void {
  for (const key of Object.keys(pickedFiles)) delete pickedFiles[key];
}
