import { describe, expect, it } from "vitest";
import { parseDiffLines } from "@/lib/diff-parser";
import { buildReview, markedSegments, splitRows, textWithRangeReverted, wordMarks, type ReviewHunk } from "@/lib/diff-review";

const lines = (n: number, from = 1) => Array.from({ length: n }, (_, i) => `line ${i + from}`);

describe("buildReview", () => {
  it("has no items when nothing changed", () => {
    expect(buildReview(parseDiffLines("a\nb\n", "a\nb\n"))).toEqual([]);
  });

  it("keeps three lines of context and folds the unchanged run between two hunks", () => {
    const before = lines(30).join("\n");
    const after = lines(30).map((l) => (l === "line 3" || l === "line 28" ? `${l}!` : l)).join("\n");
    const items = buildReview(parseDiffLines(before, after));
    expect(items.map((i) => i.kind)).toEqual(["hunk", "fold", "hunk"]);
    const hunk = items[0] as ReviewHunk;
    expect(hunk.lines.map((l) => l.content)).toEqual(["line 1", "line 2", "line 3", "line 3!", "line 4", "line 5", "line 6"]);
    expect(items[1]!.kind === "fold" && items[1]!.hidden).toBe(18);
  });

  it("folds the unchanged tail after the last hunk", () => {
    const items = buildReview(parseDiffLines(lines(20).join("\n"), ["x", ...lines(19, 2)].join("\n")));
    expect(items.map((i) => i.kind)).toEqual(["hunk", "fold"]);
  });

  it("names a hunk after the nearest line at the margin above it", () => {
    const before = ["export interface Note {", "  id: string;", "  a: 1;", "  b: 2;", "  c: 3;", "  d: 4;", "  e: 5;", "}"].join("\n");
    const after = before.replace("  e: 5;", "  e: 6;");
    const hunk = buildReview(parseDiffLines(before, after)).find((i) => i.kind === "hunk") as ReviewHunk;
    expect(hunk.name).toBe("export interface Note {");
  });

  it("marks only the changed words of a changed line", () => {
    const hunk = buildReview(parseDiffLines("const a = oldName(1);\n", "const a = newName(1);\n"))[0] as ReviewHunk;
    const removed = hunk.lines.find((l) => l.type === "remove")!;
    const added = hunk.lines.find((l) => l.type === "add")!;
    expect(markedSegments(removed.content, removed.marks).filter((s) => s.marked).map((s) => s.text)).toEqual(["oldName"]);
    expect(markedSegments(added.content, added.marks).filter((s) => s.marked).map((s) => s.text)).toEqual(["newName"]);
  });
});

describe("wordMarks", () => {
  it("marks nothing when the lines share nothing", () => {
    expect(wordMarks("abc", "xyz")).toEqual({ from: null, to: null });
  });
  it("marks only the added words on an appended line", () => {
    expect(wordMarks("run(a)", "run(a, b)")).toEqual({ from: null, to: [5, 8] });
  });
});

describe("splitRows", () => {
  it("pairs a removal with the addition after it and pads the shorter side", () => {
    const hunk = buildReview(parseDiffLines("a\nb\nc\n", "a\nB\nB2\nc\n"))[0] as ReviewHunk;
    const rows = splitRows(hunk.lines);
    expect(rows.map((r) => [r.left?.content ?? null, r.right?.content ?? null])).toEqual([
      ["a", "a"],
      ["b", "B"],
      [null, "B2"],
      ["c", "c"],
    ]);
  });
});

describe("textWithRangeReverted", () => {
  const before = "a\nb\nc\nd\ne\nf\ng\nh\ni\nj\nk\nl\n";
  const after = "a\nB\nc\nd\ne\nf\ng\nh\ni\nj\nk\nL\n";
  const diff = parseDiffLines(before, after);
  const hunks = buildReview(diff).filter((i): i is ReviewHunk => i.kind === "hunk");

  it("puts one hunk back and leaves the others as they are", () => {
    expect(hunks).toHaveLength(2);
    expect(textWithRangeReverted(diff, hunks[0]!, true)).toBe("a\nb\nc\nd\ne\nf\ng\nh\ni\nj\nk\nL\n");
  });

  it("reverting every hunk gives back the base", () => {
    expect(textWithRangeReverted(diff, { from: 0, to: diff.length }, true)).toBe(before);
  });

  describe("keeps the file's bytes outside the reverted lines", () => {
    function revertFirst(before: string, after: string): string {
      const d = parseDiffLines(before, after);
      const h = buildReview(d).filter((i): i is ReviewHunk => i.kind === "hunk");
      return textWithRangeReverted(d, h[0]!, after.endsWith("\n"), { before, after });
    }
    const body = (eol: string, mid: string) => ["a", mid, "c", "d", "e", "f", "g", "h", "i", "j"].join(eol);

    it("CRLF stays CRLF", () => {
      const before = body("\r\n", "b") + "\r\n";
      const after = body("\r\n", "B") + "\r\n";
      expect(revertFirst(before, after)).toBe(before);
    });

    it("no final newline stays without one", () => {
      expect(revertFirst(body("\n", "b"), body("\n", "B"))).toBe(body("\n", "b"));
    });

    it("a BOM stays", () => {
      const before = "\uFEFF" + body("\r\n", "b") + "\r\n";
      const after = "\uFEFF" + body("\r\n", "B") + "\r\n";
      expect(revertFirst(before, after)).toBe(before);
    });

    it("mixed endings stay as they were, line by line", () => {
      const before = "a\r\nb\nc\r\nd\ne\nf\ng\nh\ni\nj\r\n";
      const after = "a\r\nB\nc\r\nd\ne\nf\ng\nh\ni\nj\r\n";
      expect(revertFirst(before, after)).toBe(before);
    });

    it("keeps other hunks' lines untouched and a reverted hunk takes its own old ending", () => {
      const before = "a\r\nb\r\nc\r\nd\r\ne\r\nf\r\ng\r\nh\r\ni\r\nj\r\nk\r\nl\r\n";
      const after = "a\r\nB\r\nc\r\nd\r\ne\r\nf\r\ng\r\nh\r\ni\r\nj\r\nk\r\nL\r\n";
      expect(revertFirst(before, after)).toBe(after.replace("B", "b"));
    });
  });
});
