/**
 * What the Changes review draws from a file's diff lines: hunks (the changes with a little context around them), the
 * runs of unchanged lines folded between them, word-level marks on changed lines, side-by-side rows, and the text a
 * revert leaves behind.
 */
import type { DiffLine } from "@/lib/diff-parser";

const CONTEXT = 3;

/** A changed line may carry the character ranges that actually differ from its partner line. */
export interface ReviewLine extends DiffLine {
  /** Index of the line in the file's full line list. */
  index: number;
  marks?: ReadonlyArray<readonly [number, number]>;
}

export interface ReviewHunk {
  kind: "hunk";
  id: string;
  /** The nearest line above the hunk that starts at the margin: "export interface Note". */
  name: string;
  /** First line number the hunk shows (new side, or old side for a pure deletion at the top). */
  startLine: number;
  lines: ReviewLine[];
  /** Indices (into the file's full line list) of the first and last line of the hunk. */
  from: number;
  to: number;
}

export interface ReviewFold {
  kind: "fold";
  id: string;
  hidden: number;
  lines: ReviewLine[];
}

export type ReviewItem = ReviewHunk | ReviewFold;

const WORD = /(\s+|[A-Za-z0-9_$]+|[^A-Za-z0-9_$\s])/g;

function tokens(text: string): string[] {
  return text.match(WORD) ?? [];
}

/**
 * The spans of two paired lines that differ: what is left after the words they share at the start and at the end.
 * A side is null when it has nothing of its own (the other line only added to it), and both are when the lines share
 * too little, or the change covers most of the line, for marks to help.
 */
export function wordMarks(
  from: string,
  to: string,
): { from: [number, number] | null; to: [number, number] | null } {
  const a = tokens(from);
  const b = tokens(to);
  let head = 0;
  while (head < a.length && head < b.length && a[head] === b[head]) head += 1;
  let tail = 0;
  while (tail < a.length - head && tail < b.length - head && a[a.length - 1 - tail] === b[b.length - 1 - tail]) tail += 1;
  const prefix = a.slice(0, head).join("").length;
  const suffix = a.slice(a.length - tail).join("").length;
  if (prefix + suffix === 0) return { from: null, to: null };
  const fromEnd = from.length - suffix;
  const toEnd = to.length - suffix;
  const longest = Math.max(from.length, to.length);
  if (Math.max(fromEnd, toEnd) - prefix > longest * 0.7) return { from: null, to: null };
  return {
    from: fromEnd > prefix ? [prefix, fromEnd] : null,
    to: toEnd > prefix ? [prefix, toEnd] : null,
  };
}

/** Fills in `marks` for each run of removed lines followed by a run of added lines, pairing them in order. */
function markWords(lines: ReviewLine[]): void {
  let i = 0;
  while (i < lines.length) {
    if (lines[i]!.type !== "remove") {
      i += 1;
      continue;
    }
    const removes: ReviewLine[] = [];
    while (i < lines.length && lines[i]!.type === "remove") removes.push(lines[i++]!);
    const adds: ReviewLine[] = [];
    while (i < lines.length && lines[i]!.type === "add") adds.push(lines[i++]!);
    const pairs = Math.min(removes.length, adds.length);
    for (let p = 0; p < pairs; p += 1) {
      const spans = wordMarks(removes[p]!.content, adds[p]!.content);
      if (spans.from) removes[p]!.marks = [spans.from];
      if (spans.to) adds[p]!.marks = [spans.to];
    }
  }
}

function nameFor(lines: readonly ReviewLine[], before: number): string {
  for (let i = before; i >= 0; i -= 1) {
    const line = lines[i]!;
    if (line.type === "remove") continue;
    if (line.content.trim() !== "" && !/^\s/.test(line.content) && !/^[})\]]/.test(line.content)) {
      const text = line.content.trim();
      return text.length > 64 ? `${text.slice(0, 63)}…` : text;
    }
  }
  return "";
}

/** The hunks of a file's diff and the unchanged runs between them. Null-safe: no changes gives no items. */
export function buildReview(diffLines: readonly DiffLine[], context = CONTEXT): ReviewItem[] {
  const all: ReviewLine[] = diffLines.map((line, index) => ({ ...line, index }));
  const changed = all.filter((line) => line.type !== "context").map((line) => line.index);
  if (changed.length === 0) return [];

  // Group changes whose context would touch or overlap.
  const groups: Array<[number, number]> = [];
  for (const index of changed) {
    const last = groups[groups.length - 1];
    if (last && index - last[1] - 1 <= context * 2) last[1] = index;
    else groups.push([index, index]);
  }

  const items: ReviewItem[] = [];
  let cursor = 0;
  groups.forEach(([first, last], n) => {
    const from = Math.max(first - context, 0);
    const to = Math.min(last + context, all.length - 1);
    if (from > cursor) {
      const hidden = all.slice(cursor, from);
      items.push({ kind: "fold", id: `fold-${cursor}`, hidden: hidden.length, lines: hidden });
    }
    const lines = all.slice(from, to + 1);
    markWords(lines);
    const head = lines.find((line) => line.type !== "remove") ?? lines[0]!;
    items.push({
      kind: "hunk",
      id: `hunk-${n}`,
      name: nameFor(all, from),
      startLine: head.newLineNumber ?? head.oldLineNumber ?? 1,
      lines,
      from,
      to,
    });
    cursor = to + 1;
  });
  if (cursor < all.length) {
    const hidden = all.slice(cursor);
    items.push({ kind: "fold", id: `fold-${cursor}`, hidden: hidden.length, lines: hidden });
  }
  return items;
}

export interface SplitRow {
  left: ReviewLine | null;
  right: ReviewLine | null;
}

/** Old and new side by side: context on both, a run of removals against the run of additions that follows it. */
export function splitRows(lines: readonly ReviewLine[]): SplitRow[] {
  const rows: SplitRow[] = [];
  let i = 0;
  while (i < lines.length) {
    const line = lines[i]!;
    if (line.type === "context") {
      rows.push({ left: line, right: line });
      i += 1;
      continue;
    }
    const removes: ReviewLine[] = [];
    const adds: ReviewLine[] = [];
    while (i < lines.length && lines[i]!.type === "remove") removes.push(lines[i++]!);
    while (i < lines.length && lines[i]!.type === "add") adds.push(lines[i++]!);
    const count = Math.max(removes.length, adds.length);
    for (let p = 0; p < count; p += 1) rows.push({ left: removes[p] ?? null, right: adds[p] ?? null });
  }
  return rows;
}

/** Splits text into [segment, marked] pieces for a line's marks. */
export function markedSegments(
  content: string,
  marks: ReviewLine["marks"],
): Array<{ text: string; marked: boolean }> {
  if (!marks || marks.length === 0) return [{ text: content, marked: false }];
  const out: Array<{ text: string; marked: boolean }> = [];
  let at = 0;
  for (const [start, end] of marks) {
    if (start > at) out.push({ text: content.slice(at, start), marked: false });
    out.push({ text: content.slice(start, end), marked: true });
    at = end;
  }
  if (at < content.length) out.push({ text: content.slice(at), marked: false });
  return out;
}

/**
 * The file's text with one range of its diff undone: added lines inside [from, to] are dropped and removed lines
 * come back. Everything outside the range stays as the file has it now.
 */
export function textWithRangeReverted(
  diffLines: readonly DiffLine[],
  range: { from: number; to: number },
  trailingNewline: boolean,
): string {
  const out: string[] = [];
  diffLines.forEach((line, index) => {
    const inside = index >= range.from && index <= range.to;
    if (line.type === "context") out.push(line.content);
    else if (line.type === "add") {
      if (!inside) out.push(line.content);
    } else if (inside) out.push(line.content);
  });
  const text = out.join("\n");
  return trailingNewline && out.length > 0 ? `${text}\n` : text;
}
