export interface DiffLine {
  type: "add" | "remove" | "context";
  content: string;
  oldLineNumber?: number;
  newLineNumber?: number;
}

export function parseDiffLines(before: string, after: string): DiffLine[] {
  const beforeLines = splitContentLines(before);
  const afterLines = splitContentLines(after);
  const diffLines: DiffLine[] = [];

  let beforeIndex = 0;
  let afterIndex = 0;
  const context = (): void => {
    diffLines.push({
      type: "context",
      content: beforeLines[beforeIndex]!,
      oldLineNumber: beforeIndex + 1,
      newLineNumber: afterIndex + 1,
    });
    beforeIndex += 1;
    afterIndex += 1;
  };
  const remove = (): void => {
    diffLines.push({ type: "remove", content: beforeLines[beforeIndex]!, oldLineNumber: beforeIndex + 1 });
    beforeIndex += 1;
  };
  const add = (): void => {
    diffLines.push({ type: "add", content: afterLines[afterIndex]!, newLineNumber: afterIndex + 1 });
    afterIndex += 1;
  };

  for (const step of diffScript(beforeLines, afterLines)) {
    if (step === 0) context();
    else if (step === -1) remove();
    else add();
  }
  return diffLines;
}

/**
 * The edit script between two line lists: 0 keeps a line, -1 removes one from `a`, 1 adds one from `b`. Lines the two
 * share at the start and end are taken first, then Myers' O(ND) walk finds the shortest script for the middle, so a
 * big file with a few changes costs time and memory in proportion to the changes, not to its size squared.
 */
function diffScript(a: string[], b: string[]): Array<-1 | 0 | 1> {
  let head = 0;
  while (head < a.length && head < b.length && a[head] === b[head]) head += 1;
  let tail = 0;
  while (tail < a.length - head && tail < b.length - head && a[a.length - 1 - tail] === b[b.length - 1 - tail]) tail += 1;

  const script: Array<-1 | 0 | 1> = new Array<0>(head).fill(0);
  // Lines become numbers so the walk compares integers, not strings.
  const ids = new Map<string, number>();
  const idOf = (line: string): number => {
    let id = ids.get(line);
    if (id === undefined) ids.set(line, (id = ids.size));
    return id;
  };
  const midA = a.slice(head, a.length - tail).map(idOf);
  const midB = b.slice(head, b.length - tail).map(idOf);

  // A line only one side has can only be a removal or an addition, so the walk skips it: a lockfile whose changed
  // lines are all new text shrinks to the lines both sides share.
  const inB = new Set(midB);
  const inA = new Set(midA);
  const keptA: number[] = [];
  const keptB: number[] = [];
  midA.forEach((id, index) => {
    if (inB.has(id)) keptA.push(index);
  });
  midB.forEach((id, index) => {
    if (inA.has(id)) keptB.push(index);
  });
  const steps = myers(
    keptA.map((index) => midA[index]!),
    keptB.map((index) => midB[index]!),
  );

  let pa = 0;
  let pb = 0;
  let i = 0;
  let j = 0;
  const removesUpTo = (end: number): void => {
    for (; pa < end; pa += 1) script.push(-1);
  };
  const addsUpTo = (end: number): void => {
    for (; pb < end; pb += 1) script.push(1);
  };
  for (const step of steps) {
    if (step === 0) {
      removesUpTo(keptA[i]!);
      addsUpTo(keptB[j]!);
      script.push(0);
      pa += 1;
      pb += 1;
      i += 1;
      j += 1;
    } else if (step === -1) {
      removesUpTo(keptA[i]! + 1);
      i += 1;
    } else {
      removesUpTo(i < keptA.length ? keptA[i]! : midA.length);
      addsUpTo(keptB[j]! + 1);
      j += 1;
    }
  }
  removesUpTo(midA.length);
  addsUpTo(midB.length);
  for (let i = 0; i < tail; i += 1) script.push(0);
  return script;
}

/** Past this many changed lines the middle is shown as all removed then all added rather than searched for. */
const MAX_EDIT_DISTANCE = 6000;

function myers(a: number[], b: number[]): Array<-1 | 0 | 1> {
  const n = a.length;
  const m = b.length;
  if (n === 0) return new Array<1>(m).fill(1);
  if (m === 0) return new Array<-1>(n).fill(-1);

  const max = Math.min(n + m, MAX_EDIT_DISTANCE);
  const offset = max + 1;
  const v = new Int32Array(2 * max + 3);
  // trace[d] holds the furthest x on diagonals -d..d (index k + d) after round d.
  const trace: Int32Array[] = [];
  let found = -1;
  for (let d = 0; d <= max && found < 0; d += 1) {
    for (let k = -d; k <= d; k += 2) {
      let x: number;
      if (k === -d || (k !== d && v[offset + k - 1]! < v[offset + k + 1]!)) x = v[offset + k + 1]!;
      else x = v[offset + k - 1]! + 1;
      let y = x - k;
      while (x < n && y < m && a[x] === b[y]) {
        x += 1;
        y += 1;
      }
      v[offset + k] = x;
      if (x >= n && y >= m) {
        found = d;
        break;
      }
    }
    trace.push(v.slice(offset - d, offset + d + 1));
  }
  if (found < 0) return [...new Array<-1>(n).fill(-1), ...new Array<1>(m).fill(1)];

  const reversed: Array<-1 | 0 | 1> = [];
  let x = n;
  let y = m;
  for (let d = found; d > 0; d -= 1) {
    const prev = trace[d - 1]!; // diagonals -(d-1)..(d-1), index k + d - 1
    const k = x - y;
    const at = (diag: number): number => prev[diag + d - 1]!;
    const down = k === -d || (k !== d && at(k - 1) < at(k + 1));
    const prevK = down ? k + 1 : k - 1;
    const prevX = at(prevK);
    const prevY = prevX - prevK;
    while (x > (down ? prevX : prevX + 1) && y > (down ? prevY + 1 : prevY)) {
      reversed.push(0);
      x -= 1;
      y -= 1;
    }
    reversed.push(down ? 1 : -1);
    if (down) y -= 1;
    else x -= 1;
  }
  while (x > 0 && y > 0) {
    reversed.push(0);
    x -= 1;
    y -= 1;
  }
  return reversed.reverse();
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
