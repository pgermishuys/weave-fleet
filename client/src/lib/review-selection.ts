import type { LineRange } from "@/lib/code-editor/line-pick";

/** A diff row a selection touches: its line number on each side, null where the side has no such line. */
export interface SelectedRow {
  old: number | null;
  new: number | null;
}

/** The lines a selection in a diff spans, on one side: new (the file as it is) or old (as it was). */
export interface ReviewSelection {
  side: "old" | "new";
  range: LineRange;
}

/**
 * Which lines the selected rows stand for. A selection that takes in any line the file still has is about the file
 * as it is, so it counts in new line numbers (removed lines in it have none, and drop out); one that holds removed
 * lines only is about the old file, in old line numbers.
 */
export function selectionLines(rows: readonly SelectedRow[]): ReviewSelection | null {
  const side = rows.some((row) => row.new !== null) ? "new" : "old";
  const numbers = rows.map((row) => row[side]).filter((number): number is number => number !== null);
  if (numbers.length === 0) return null;
  return { side, range: { first: Math.min(...numbers), last: Math.max(...numbers) } };
}

export function sameSelection(a: ReviewSelection | null, b: ReviewSelection | null): boolean {
  return a === b || (!!a && !!b && a.side === b.side && a.range.first === b.range.first && a.range.last === b.range.last);
}
