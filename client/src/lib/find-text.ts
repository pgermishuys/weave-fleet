/** What `GET /api/sessions/{id}/find/text` answers: lines that have the text, grouped by file. */
export interface TextRange {
  start: number;
  end: number;
}

export interface TextMatch {
  line: number;
  column: number;
  /** The line, cut around the match when it is long; `ranges` index into this. */
  text: string;
  ranges: TextRange[];
}

export interface TextFile {
  path: string;
  /** Every matching line found in the file, which can be more than `matches` holds. */
  total: number;
  matches: TextMatch[];
}

export interface TextSearchResult {
  files: TextFile[];
  matches: number;
  /** Null when the search ran to the end; "limit" at the cap; "deadline" when it ran out of time. */
  stopped: "limit" | "deadline" | null;
  engine: "git" | "walk";
}

/** Matches shown per file before "Show N more in this file". */
export const MATCHES_PER_FILE = 5;

export interface TextSegment {
  text: string;
  marked: boolean;
}

/** The line cut into plain and marked pieces, so it can be drawn without HTML. */
export function segmentsOf(text: string, ranges: readonly TextRange[]): TextSegment[] {
  const segments: TextSegment[] = [];
  let at = 0;
  for (const range of [...ranges].sort((a, b) => a.start - b.start)) {
    const start = Math.max(range.start, at);
    const end = Math.min(range.end, text.length);
    if (end <= start) continue;
    if (start > at) segments.push({ text: text.slice(at, start), marked: false });
    segments.push({ text: text.slice(start, end), marked: true });
    at = end;
  }
  if (at < text.length) segments.push({ text: text.slice(at), marked: false });
  return segments;
}

/** A matching line as the list shows it, in the order the keys step through them. */
export interface VisibleHit {
  path: string;
  line: number;
}

/** The lines on screen: the first few per file, or all of them for a file that was expanded. */
export function visibleHits(files: readonly TextFile[], expanded: ReadonlySet<string>): VisibleHit[] {
  return files.flatMap((file) =>
    (expanded.has(file.path) ? file.matches : file.matches.slice(0, MATCHES_PER_FILE)).map((match) => ({
      path: file.path,
      line: match.line,
    })),
  );
}
