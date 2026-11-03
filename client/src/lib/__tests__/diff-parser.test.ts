import { describe, expect, it } from "vitest";
import { parseDiffLines, parseUnifiedDiff } from "@/lib/diff-parser";

describe("parseDiffLines", () => {
  it("returns_all_context_lines_for_identical_content", () => {
    const lines = parseDiffLines("first\nsecond\nthird", "first\nsecond\nthird");

    expect(lines).toEqual([
      { type: "context", content: "first", oldLineNumber: 1, newLineNumber: 1 },
      { type: "context", content: "second", oldLineNumber: 2, newLineNumber: 2 },
      { type: "context", content: "third", oldLineNumber: 3, newLineNumber: 3 },
    ]);
  });

  it("returns_added_lines_for_added_file", () => {
    const lines = parseDiffLines("", "first\nsecond");

    expect(lines).toEqual([
      { type: "add", content: "first", newLineNumber: 1 },
      { type: "add", content: "second", newLineNumber: 2 },
    ]);
  });

  it("returns_removed_lines_for_deleted_file", () => {
    const lines = parseDiffLines("first\nsecond", "");

    expect(lines).toEqual([
      { type: "remove", content: "first", oldLineNumber: 1 },
      { type: "remove", content: "second", oldLineNumber: 2 },
    ]);
  });

  it("returns_mixed_adds_removes_and_context_for_modified_file", () => {
    const lines = parseDiffLines(
      "alpha\nbeta\ngamma\nepsilon",
      "alpha\nbeta changed\ngamma\ndelta\nepsilon",
    );

    expect(lines).toEqual([
      { type: "context", content: "alpha", oldLineNumber: 1, newLineNumber: 1 },
      { type: "remove", content: "beta", oldLineNumber: 2 },
      { type: "add", content: "beta changed", newLineNumber: 2 },
      { type: "context", content: "gamma", oldLineNumber: 3, newLineNumber: 3 },
      { type: "add", content: "delta", newLineNumber: 4 },
      { type: "context", content: "epsilon", oldLineNumber: 4, newLineNumber: 5 },
    ]);
  });
});

describe("parseUnifiedDiff (real git patches)", () => {
  it("reads_removed_and_added_lines_that_look_like_file_headers_as_lines", () => {
    // git diff of: "-- sql comment" -> "new" and "++ x" -> "++ x2" (the lines arrive as "--- …" and "+++ …").
    const patch = [
      "diff --git a/f.txt b/f.txt",
      "index e683d13..38f6880 100644",
      "--- a/f.txt",
      "+++ b/f.txt",
      "@@ -2,6 +2,6 @@ a",
      " ",
      " b",
      " - bullet",
      "--- sql comment",
      "-++ x",
      "+new",
      "+++ x2",
      " c",
      "",
    ].join("\n");

    const { lines, paths, created } = parseUnifiedDiff(patch);

    expect(paths).toEqual(["f.txt"]);
    expect(created).toBe(false);
    expect(lines.map((line) => [line.type, line.content, line.oldLineNumber, line.newLineNumber])).toEqual([
      ["context", "", 2, 2],
      ["context", "b", 3, 3],
      ["context", "- bullet", 4, 4],
      ["remove", "-- sql comment", 5, undefined],
      ["remove", "++ x", 6, undefined],
      ["add", "new", undefined, 5],
      ["add", "++ x2", undefined, 6],
      ["context", "c", 7, 7],
    ]);
  });

  it("keeps_line_numbers_when_a_blank_context_line_is_an_empty_line", () => {
    // git -c diff.suppressBlankEmpty=true prints a blank context line as "" instead of " ".
    const patch = [
      "--- a/f.txt",
      "+++ b/f.txt",
      "@@ -2,4 +2,4 @@ a",
      "",
      " b",
      "-old",
      "+new",
      " c",
      "",
    ].join("\n");

    const { lines } = parseUnifiedDiff(patch);

    expect(lines.map((line) => [line.type, line.content, line.oldLineNumber, line.newLineNumber])).toEqual([
      ["context", "", 2, 2],
      ["context", "b", 3, 3],
      ["remove", "old", 4, undefined],
      ["add", "new", undefined, 4],
      ["context", "c", 5, 5],
    ]);
  });

  it("keeps_the_lines_of_a_deleted_file_that_start_with_a_dash_or_a_space", () => {
    const patch = [
      "--- a/g.txt",
      "+++ /dev/null",
      "@@ -1,3 +0,0 @@",
      "-  indented",
      "--- item",
      "-- item",
      "",
    ].join("\n");

    const { lines } = parseUnifiedDiff(patch);

    expect(lines.map((line) => line.content)).toEqual(["  indented", "-- item", "- item"]);
  });
});
