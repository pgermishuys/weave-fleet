import type { LineRange } from "@/lib/code-editor/line-pick";

/** "L7" or "L18–27". */
export function lineRangeLabel(range: LineRange): string {
  return range.first === range.last ? `L${range.first}` : `L${range.first}–${range.last}`;
}

/** "1 line" or "10 lines". */
export function lineCountLabel(range: LineRange): string {
  const count = range.last - range.first + 1;
  return count === 1 ? "1 line" : `${count} lines`;
}

/** "src/app.ts:7" or "src/app.ts:18-27", the form a message's @ reference takes. */
export function lineReference(path: string, range: LineRange): string {
  return `${path}:${range.first === range.last ? range.first : `${range.first}-${range.last}`}`;
}

/** Lines `first`–`last` of `text`, without their line endings. */
export function sliceLines(text: string, range: LineRange): string {
  return text.split("\n").slice(range.first - 1, range.last).map((line) => line.replace(/\r$/, "")).join("\n");
}

/** A heading, then the lines in a fence that's longer than any run of backticks inside them. */
export function lineQuote(heading: string, path: string, text: string): string {
  const longest = Math.max(0, ...(text.match(/`+/g) ?? []).map((run) => run.length));
  const fence = "`".repeat(Math.max(3, longest + 1));
  const name = path.slice(path.lastIndexOf("/") + 1);
  const extension = /\.([a-z0-9]+)$/i.exec(name)?.[1]?.toLowerCase() ?? "";
  return `${heading}\n${fence}${extension}\n${text}\n${fence}\n`;
}
