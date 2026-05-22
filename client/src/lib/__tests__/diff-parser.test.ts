import { describe, expect, it } from "vitest";
import { parseDiffLines } from "@/lib/diff-parser";

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
