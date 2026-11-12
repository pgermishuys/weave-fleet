/**
 * Find in Changes: what the find bar searches in a session's diffs, and how a match is marked and brought into view.
 *
 * The search reads the diff data, not the page, so it covers files that are collapsed, runs of unchanged lines that are
 * folded and lines past a long diff's "Show more". A match there opens what hides it when the bar steps to it. The marks
 * are drawn with the CSS Custom Highlight API under their own names, so they never clear the conversation's.
 */
import { countOccurrences, rangesIn } from "@/lib/conversation-find";
import type { ReviewItem } from "@/lib/diff-review";

/** One line of a file's diff the bar can land on. */
export interface FindLine {
  /** The line's index in the file's full line list (unique within the file, drawn as `data-find-line`). */
  index: number;
  text: string;
  /** The hunk or fold it sits in. */
  itemId: string;
  inFold: boolean;
}

export interface FindFile {
  path: string;
  lines: readonly FindLine[];
}

export interface ChangesHit {
  path: string;
  line: number;
  itemId: string;
  inFold: boolean;
  /** Which of the line's matches this is, from the left. */
  ordinal: number;
}

/** A file's lines in the order the review draws them: each hunk's lines, and the unchanged runs folded between them. */
export function findLines(items: readonly ReviewItem[]): FindLine[] {
  const lines: FindLine[] = [];
  for (const item of items) {
    for (const line of item.lines) {
      lines.push({ index: line.index, text: line.content, itemId: item.id, inFold: item.kind === "fold" });
    }
  }
  return lines;
}

/** Every match in the files, top to bottom. Each line counts once, though split view draws an unchanged line twice. */
export function findChangesHits(files: readonly FindFile[], query: string): ChangesHit[] {
  if (query.trim() === "") return [];
  const hits: ChangesHit[] = [];
  for (const file of files) {
    for (const line of file.lines) {
      const count = countOccurrences(line.text, query);
      for (let ordinal = 0; ordinal < count; ordinal += 1) {
        hits.push({ path: file.path, line: line.index, itemId: line.itemId, inFold: line.inFold, ordinal });
      }
    }
  }
  return hits;
}

/** Ctrl/⌘G and F3 step through the matches, with Shift going back, as in a file tab. Returns the direction, or 0. */
export function stepShortcut(event: KeyboardEvent, apple: boolean): 1 | -1 | 0 {
  const mod = apple ? event.metaKey && !event.ctrlKey : event.ctrlKey && !event.metaKey;
  const isStep = (event.key === "F3" && !event.ctrlKey && !event.metaKey) || (mod && event.key.toLowerCase() === "g");
  if (!isStep || event.altKey) return 0;
  return event.shiftKey ? -1 : 1;
}

/** The text selected inside `root`, when it is on one line and short enough to search for. */
export function selectedQuery(root: Element | null): string {
  const selection = typeof window !== "undefined" ? window.getSelection() : null;
  if (!root || !selection || selection.rangeCount === 0 || selection.isCollapsed) return "";
  if (!root.contains(selection.anchorNode) || !root.contains(selection.focusNode)) return "";
  const text = selection.toString();
  return text.includes("\n") || text.length > 200 ? "" : text;
}

// ─── Marks ──────────────────────────────────────────────────────────────────

export const CHANGES_HIT = "changes-find-hit";
export const CHANGES_CURRENT = "changes-find-current";

interface HighlightRegistry {
  set(name: string, value: unknown): void;
  delete(name: string): void;
}

function setHighlight(name: string, ranges: readonly Range[]): void {
  const highlights = (globalThis as { CSS?: { highlights?: HighlightRegistry } }).CSS?.highlights;
  const HighlightCtor = (globalThis as { Highlight?: new (...ranges: Range[]) => unknown }).Highlight;
  if (!highlights) return;
  if (HighlightCtor && ranges.length > 0) highlights.set(name, new HighlightCtor(...ranges));
  else highlights.delete(name);
}

/** The drawn copies of each line, by file and line index (split view draws an unchanged line on both sides). */
function drawnLines(body: Element): Map<string, Element[]> {
  const out = new Map<string, Element[]>();
  for (const section of body.querySelectorAll<HTMLElement>("[data-review-file]")) {
    const path = section.dataset.reviewFile ?? "";
    for (const element of section.querySelectorAll<HTMLElement>("[data-find-line]")) {
      const key = `${path}\u0000${element.dataset.findLine}`;
      const list = out.get(key);
      if (list) list.push(element);
      else out.set(key, [element]);
    }
  }
  return out;
}

/**
 * Marks every drawn match and the current one more strongly. Returns the current match's first drawn copy, so the
 * caller can scroll to it (null when it isn't drawn yet).
 */
export function markChangesMatches(body: Element, hits: readonly ChangesHit[], query: string, current: ChangesHit | null): Range | null {
  const lines = drawnLines(body);
  const all: Range[] = [];
  const currentRanges: Range[] = [];
  const done = new Set<string>();
  for (const hit of hits) {
    const key = `${hit.path}\u0000${hit.line}`;
    if (done.has(key)) continue;
    done.add(key);
    for (const element of lines.get(key) ?? []) {
      const ranges = rangesIn(element, query);
      all.push(...ranges);
      if (current && current.path === hit.path && current.line === hit.line) {
        const range = ranges[current.ordinal];
        if (range) currentRanges.push(range);
      }
    }
  }
  setHighlight(CHANGES_HIT, all);
  setHighlight(CHANGES_CURRENT, currentRanges);
  return currentRanges[0] ?? null;
}

export function clearChangesMarks(): void {
  setHighlight(CHANGES_HIT, []);
  setHighlight(CHANGES_CURRENT, []);
}

/**
 * Scrolls `container` (and a line's own sideways scroller, when lines don't wrap) so the range sits in view, a little
 * below the sticky file header. Leaves it alone when it is already in view.
 */
export function scrollRangeIntoView(range: Range, container: HTMLElement, topInset = 48): void {
  const rect = range.getBoundingClientRect();
  const start = range.startContainer instanceof Element ? range.startContainer : range.startContainer.parentElement;
  const sideways = start?.closest<HTMLElement>(".rscroll, .rsplit");
  if (sideways && sideways.scrollWidth > sideways.clientWidth) {
    const box = sideways.getBoundingClientRect();
    // In unified view the line numbers stick to the left edge: the text shows from where they end. The right edge fades.
    const line = start?.closest(".rline");
    const text = start?.closest(".rline__tx");
    const gutter = sideways.matches(".rscroll") && line && text ? text.getBoundingClientRect().left - line.getBoundingClientRect().left : 0;
    const left = box.left + gutter + 8;
    const right = box.right - 32;
    // As little as it takes, like a browser's find: a match already in view doesn't move.
    if (rect.right > right) sideways.scrollLeft += Math.min(rect.right - right + 24, rect.left - left);
    else if (rect.left < left) sideways.scrollLeft -= left - rect.left + 24;
  }
  const box = container.getBoundingClientRect();
  const after = range.getBoundingClientRect();
  if (after.top < box.top + topInset || after.bottom > box.bottom - 16) {
    container.scrollTop += after.top - box.top - box.height / 2;
  }
}
