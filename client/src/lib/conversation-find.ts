/**
 * Find in this conversation: what the find bar searches, and how a hit is marked on screen.
 *
 * The search reads the conversation's message data, not the page, so it covers turns that are folded and rows that
 * aren't drawn. The marks are drawn on the page with the CSS Custom Highlight API, which paints ranges over text
 * without touching the DOM Vue owns.
 */

/** What the bar searches. Thinking text is never searched. */
export type FindScope = "messages" | "tools";

/** One thing the search can land in: a message of the conversation, with the text it shows and the output its calls hold. */
export interface FindUnit {
  /** The id the message is drawn with (`data-message-id`, or `data-find-id` on the phone). */
  id: string;
  /** The turn the message belongs to when that turn can be folded; a hit in it opens the turn. */
  turnId: string | null;
  /** What the message says. */
  text: string;
  /** What its tool calls printed. */
  toolText: string[];
}

export interface FindHit {
  unitId: string;
  turnId: string | null;
  /** Which of the message's matches this is, in the order they're drawn (text first, then tool output). */
  ordinal: number;
  kind: "text" | "tool";
}

/** Typing shorter than this finds nothing: one letter matches almost everything. */
export const FIND_MIN_CHARS = 1;

/** How many times `needle` is in `haystack`, ignoring case, without overlapping. */
export function countOccurrences(haystack: string, needle: string): number {
  if (!needle || !haystack) return 0;
  const lower = haystack.toLowerCase();
  const target = needle.toLowerCase();
  let count = 0;
  let from = 0;
  for (;;) {
    const at = lower.indexOf(target, from);
    if (at < 0) return count;
    count += 1;
    from = at + target.length;
  }
}

/** Every match in the conversation, oldest first. */
export function findHits(units: readonly FindUnit[], query: string, scope: FindScope): FindHit[] {
  const needle = query.trim().length >= FIND_MIN_CHARS ? query : "";
  if (!needle) return [];
  const hits: FindHit[] = [];
  for (const unit of units) {
    let ordinal = 0;
    for (let i = countOccurrences(unit.text, needle); i > 0; i -= 1) {
      hits.push({ unitId: unit.id, turnId: unit.turnId, ordinal: ordinal++, kind: "text" });
    }
    if (scope === "tools") {
      for (const output of unit.toolText) {
        for (let i = countOccurrences(output, needle); i > 0; i -= 1) {
          hits.push({ unitId: unit.id, turnId: unit.turnId, ordinal: ordinal++, kind: "tool" });
        }
      }
    }
  }
  return hits;
}

// ─── Focus ──────────────────────────────────────────────────────────────────

/** The session composer's text area. It has no Ctrl/⌘F of its own, so it counts as part of the conversation. */
export const COMPOSER_SELECTOR = "textarea.composer-frame__textarea";

/** Where focus sits decides who owns Ctrl/⌘F: a field, an editor and the terminal keep the key's own meaning. */
export function focusKeepsFindKey(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  if (target.matches(COMPOSER_SELECTOR)) return false;
  if (target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement || target instanceof HTMLSelectElement) return true;
  return target.isContentEditable || target.closest(".cm-editor, .xterm, [contenteditable='true'], [role='dialog']") !== null;
}

/** Ctrl+F (⌘F on a Mac) with nothing else held. */
export function isFindShortcut(event: KeyboardEvent, apple: boolean): boolean {
  return event.key.toLowerCase() === "f" && !event.shiftKey && !event.altKey && (apple ? event.metaKey && !event.ctrlKey : event.ctrlKey && !event.metaKey);
}

// ─── Marks ──────────────────────────────────────────────────────────────────

const ALL = "find-hit";
const CURRENT = "find-current";

interface HighlightRegistry {
  set(name: string, value: unknown): void;
  delete(name: string): void;
}

function registry(): HighlightRegistry | null {
  const css = (globalThis as { CSS?: { highlights?: HighlightRegistry } }).CSS;
  return css?.highlights ?? null;
}

const SKIPPED = "button, textarea, input, script, style, [data-find-skip]";

function textNodes(root: Element): Text[] {
  const nodes: Text[] = [];
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    if (node.parentElement?.closest(SKIPPED)) continue;
    nodes.push(node as Text);
  }
  return nodes;
}

/** The ranges of `needle` in an element's drawn text, in reading order (a match may run across inline elements). */
export function rangesIn(root: Element, needle: string): Range[] {
  if (!needle) return [];
  const nodes = textNodes(root);
  const joined = nodes.map((node) => node.data).join("").toLowerCase();
  const target = needle.toLowerCase();
  const ranges: Range[] = [];
  let from = 0;
  for (;;) {
    const at = joined.indexOf(target, from);
    if (at < 0) return ranges;
    const range = document.createRange();
    let offset = 0;
    let started = false;
    for (const node of nodes) {
      const end = offset + node.data.length;
      if (!started && at < end) {
        range.setStart(node, at - offset);
        started = true;
      }
      if (started && at + target.length <= end) {
        range.setEnd(node, at + target.length - offset);
        break;
      }
      offset = end;
    }
    ranges.push(range);
    from = at + target.length;
  }
}

/**
 * Marks every match in the given elements and the current one more strongly. Returns the current range, so the caller
 * can scroll to it (null when the current match isn't drawn, e.g. inside a collapsed row).
 */
export function markMatches(elements: readonly Element[], needle: string, current: { element: Element | null; ordinal: number }): Range | null {
  const highlights = registry();
  const all: Range[] = [];
  let currentRange: Range | null = null;
  for (const element of elements) {
    const ranges = rangesIn(element, needle);
    all.push(...ranges);
    if (element === current.element) currentRange = ranges[Math.min(current.ordinal, ranges.length - 1)] ?? null;
  }
  if (highlights) {
    const HighlightCtor = (globalThis as { Highlight?: new (...ranges: Range[]) => unknown }).Highlight;
    if (HighlightCtor && all.length > 0) highlights.set(ALL, new HighlightCtor(...all));
    else highlights.delete(ALL);
    if (HighlightCtor && currentRange) highlights.set(CURRENT, new HighlightCtor(currentRange));
    else highlights.delete(CURRENT);
  }
  return currentRange;
}

export function clearMarks(): void {
  const highlights = registry();
  highlights?.delete(ALL);
  highlights?.delete(CURRENT);
}
