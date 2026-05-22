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
