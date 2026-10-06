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
 * Parse a unified diff (what most harnesses attach to an edit) into lines. Hunk headers give the
 * line numbers; "--- /dev/null" means the file is new.
 */
export function parseUnifiedDiff(patch: string): { lines: DiffLine[]; created: boolean; paths: string[] } {
  const lines: DiffLine[] = [];
  const paths: string[] = [];
  let created = false;
  let oldLine = 0;
  let newLine = 0;

  for (const raw of patch.split("\n")) {
    const hunk = /^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/.exec(raw);
    if (hunk) {
      oldLine = Number(hunk[1]);
      newLine = Number(hunk[2]);
      continue;
    }

    if (raw.startsWith("--- ")) {
      if (raw.slice(4).trim() === "/dev/null") created = true;
      continue;
    }

    if (raw.startsWith("+++ ")) {
      const path = stripPatchPathPrefix(raw.slice(4).trim());
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

function stripPatchPathPrefix(path: string): string {
  const withoutTab = path.split("\t")[0] ?? path;
  return withoutTab.replace(/^[ab]\//, "");
}
