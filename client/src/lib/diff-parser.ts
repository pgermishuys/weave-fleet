export interface DiffLine {
  type: "add" | "remove" | "context";
  content: string;
  oldLineNumber?: number;
  newLineNumber?: number;
}

export function parseDiffLines(before: string, after: string): DiffLine[] {
  const beforeLines = splitContentLines(before);
  const afterLines = splitContentLines(after);
  const lcsLengths = buildLcsLengths(beforeLines, afterLines);
  const diffLines: DiffLine[] = [];

  let beforeIndex = 0;
  let afterIndex = 0;

  while (beforeIndex < beforeLines.length || afterIndex < afterLines.length) {
    const beforeLine = beforeLines[beforeIndex];
    const afterLine = afterLines[afterIndex];

    if (beforeLine !== undefined && afterLine !== undefined && beforeLine === afterLine) {
      diffLines.push({
        type: "context",
        content: beforeLine,
        oldLineNumber: beforeIndex + 1,
        newLineNumber: afterIndex + 1,
      });
      beforeIndex += 1;
      afterIndex += 1;
      continue;
    }

    if (
      beforeLine !== undefined &&
      (afterLine === undefined || lcsLengths[beforeIndex + 1]![afterIndex]! >= lcsLengths[beforeIndex]![afterIndex + 1]!)
    ) {
      diffLines.push({
        type: "remove",
        content: beforeLine,
        oldLineNumber: beforeIndex + 1,
      });
      beforeIndex += 1;
      continue;
    }

    if (afterLine !== undefined) {
      diffLines.push({
        type: "add",
        content: afterLine,
        newLineNumber: afterIndex + 1,
      });
      afterIndex += 1;
    }
  }

  return diffLines;
}

function splitContentLines(content: string): string[] {
  if (content === "") {
    return [];
  }

  const lines = content.replaceAll("\r\n", "\n").replaceAll("\r", "\n").split("\n");

  if (lines[lines.length - 1] === "") {
    lines.pop();
  }

  return lines;
}

function buildLcsLengths(beforeLines: string[], afterLines: string[]): number[][] {
  const lcsLengths = Array.from({ length: beforeLines.length + 1 }, () =>
    Array.from({ length: afterLines.length + 1 }, () => 0),
  );

  for (let beforeIndex = beforeLines.length - 1; beforeIndex >= 0; beforeIndex -= 1) {
    for (let afterIndex = afterLines.length - 1; afterIndex >= 0; afterIndex -= 1) {
      lcsLengths[beforeIndex]![afterIndex] = beforeLines[beforeIndex] === afterLines[afterIndex]
        ? lcsLengths[beforeIndex + 1]![afterIndex + 1]! + 1
        : Math.max(lcsLengths[beforeIndex + 1]![afterIndex]!, lcsLengths[beforeIndex]![afterIndex + 1]!);
    }
  }

  return lcsLengths;
}

/**
 * Follows a unified diff's hunks by the line counts in each "@@ -a,b +c,d @@" header, so a hunk's own lines are never
 * mistaken for something else: a removed "-- x" arrives as "--- x" and an added "++ x" as "+++ x" (both look like file
 * headers), and with git's diff.suppressBlankEmpty a blank context line is an empty line, not " ".
 */
export class HunkTracker {
  private oldLeft = 0;
  private newLeft = 0;

  /** True while the hunk still owes lines: whatever comes next is that hunk's content, not a header. */
  get inside(): boolean {
    return this.oldLeft > 0 || this.newLeft > 0;
  }

  /** Reads a hunk header; returns its starting line numbers, or null when the line isn't one. */
  start(raw: string): { oldLine: number; newLine: number } | null {
    const hunk = /^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@/.exec(raw);
    if (!hunk) return null;
    this.oldLeft = hunk[2] === undefined ? 1 : Number(hunk[2]);
    this.newLeft = hunk[4] === undefined ? 1 : Number(hunk[4]);
    return { oldLine: Number(hunk[1]), newLine: Number(hunk[3]) };
  }

  /** Counts a line of the hunk off; call it for every line while {@link inside}. */
  consume(type: DiffLine["type"]): void {
    if (type !== "add") this.oldLeft = Math.max(0, this.oldLeft - 1);
    if (type !== "remove") this.newLeft = Math.max(0, this.newLeft - 1);
  }

  /** A line ending the hunk early (a new file's "diff --git"): the counts were wrong, stop trusting them. */
  reset(): void {
    this.oldLeft = 0;
    this.newLeft = 0;
  }
}

/**
 * Parse a unified diff (what most harnesses attach to an edit) into lines. Hunk headers give the
 * line numbers; "--- /dev/null" means the file is new.
 */
export function parseUnifiedDiff(patch: string): { lines: DiffLine[]; created: boolean; paths: string[] } {
  const lines: DiffLine[] = [];
  const paths: string[] = [];
  let created = false;
  let oldHeader = "";
  let oldLine = 0;
  let newLine = 0;
  const hunks = new HunkTracker();

  const rows = patch.split("\n");
  for (const [index, raw] of rows.entries()) {
    if (raw.startsWith("diff ")) hunks.reset();
    // The newline ending the patch isn't a blank context line, even when a harness's hunk header counts too many.
    if (raw === "" && index === rows.length - 1) continue;

    if (hunks.inside && !raw.startsWith("\\ ")) {
      const type = raw.startsWith("+") ? "add" : raw.startsWith("-") ? "remove" : "context";
      hunks.consume(type);
      const content = raw.slice(1);
      if (type === "add") {
        lines.push({ type, content, newLineNumber: newLine });
        newLine += 1;
      } else if (type === "remove") {
        lines.push({ type, content, oldLineNumber: oldLine });
        oldLine += 1;
      } else {
        lines.push({ type, content, oldLineNumber: oldLine, newLineNumber: newLine });
        oldLine += 1;
        newLine += 1;
      }
      continue;
    }

    const hunk = hunks.start(raw);
    if (hunk) {
      oldLine = hunk.oldLine;
      newLine = hunk.newLine;
      continue;
    }

    if (raw.startsWith("--- ")) {
      oldHeader = raw.slice(4).trim();
      if (oldHeader === "/dev/null") created = true;
      continue;
    }

    if (raw.startsWith("+++ ")) {
      const path = stripPatchPathPrefix(raw.slice(4).trim(), oldHeader);
      if (path && path !== "/dev/null") paths.push(path);
      continue;
    }

    // OpenCode's apply_patch names its files in its own header.
    const applyPatch = /^\*\*\* (Add|Update|Delete) File: (.+)$/.exec(raw);
    if (applyPatch) {
      if (applyPatch[1] === "Add") created = true;
      paths.push(applyPatch[2]!.trim());
      continue;
    }

    if (raw.startsWith("diff ") || raw.startsWith("index ") || raw.startsWith("\\ ")) continue;

    if (raw.startsWith("+")) {
      lines.push({ type: "add", content: raw.slice(1), newLineNumber: newLine });
      newLine += 1;
      continue;
    }

    if (raw.startsWith("-")) {
      lines.push({ type: "remove", content: raw.slice(1), oldLineNumber: oldLine });
      oldLine += 1;
      continue;
    }

    if (raw.startsWith(" ")) {
      lines.push({ type: "context", content: raw.slice(1), oldLineNumber: oldLine, newLineNumber: newLine });
      oldLine += 1;
      newLine += 1;
    }
  }

  return { lines, created, paths };
}

/**
 * The path a "+++" line names, without git's prefix. Git writes a/ and b/, or with diff.mnemonicPrefix c/ (commit),
 * i/ (index), w/ (worktree) and o/ (object); diff.noprefix writes none. A prefix is only taken off when the "---"
 * line before it carries a matching one (or the file is new), so a real folder called "w" survives noprefix.
 */
function stripPatchPathPrefix(path: string, oldHeader: string): string {
  const withoutTab = path.split("\t")[0] ?? path;
  const before = (oldHeader.split("\t")[0] ?? "").trim();
  const prefixed = /^([bwio2])\/(.+)$/.exec(withoutTab);
  if (!prefixed) return withoutTab;
  const [, , rest] = prefixed;
  const old = /^[acio1]\/(.+)$/.exec(before);
  return before === "/dev/null" || before === "" ? rest! : old && old[1] === rest ? rest! : withoutTab;
}
