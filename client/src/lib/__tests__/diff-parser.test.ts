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

describe("parseUnifiedDiff paths", () => {
  it.each([
    ["default", "a/src/f.ts", "b/src/f.ts"],
    ["diff.mnemonicPrefix (commit to worktree)", "c/src/f.ts", "w/src/f.ts"],
    ["diff.mnemonicPrefix (index to worktree)", "i/src/f.ts", "w/src/f.ts"],
    ["diff.mnemonicPrefix (commit to index)", "c/src/f.ts", "i/src/f.ts"],
    ["diff.noprefix", "src/f.ts", "src/f.ts"],
  ])("names_the_file_without_a_prefix_for_%s", (_name, oldPath, newPath) => {
    const { paths } = parseUnifiedDiff(`--- ${oldPath}\n+++ ${newPath}\n@@ -1 +1 @@\n-a\n+b\n`);

    expect(paths).toEqual(["src/f.ts"]);
  });

  it("names_a_new_file_under_mnemonic_prefixes", () => {
    const { paths, created } = parseUnifiedDiff("--- /dev/null\n+++ w/h.txt\n@@ -0,0 +1 @@\n+x\n");

    expect(created).toBe(true);
    expect(paths).toEqual(["h.txt"]);
  });

  it("keeps_a_folder_named_like_a_prefix_when_git_writes_no_prefix", () => {
    const { paths } = parseUnifiedDiff("--- w/f.ts\n+++ w/f.ts\n@@ -1 +1 @@\n-a\n+b\n");

    expect(paths).toEqual(["w/f.ts"]);
  });
});

describe("parseDiffLines on big and random inputs", () => {
  function rebuild(lines: ReturnType<typeof parseDiffLines>) {
    return {
      before: lines.filter((l) => l.type !== "add").map((l) => l.content),
      after: lines.filter((l) => l.type !== "remove").map((l) => l.content),
    };
  }

  it("always rebuilds both sides from the lines, for random edits", () => {
    let seed = 7;
    const rnd = () => (seed = (seed * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff;
    for (let round = 0; round < 200; round += 1) {
      const before = Array.from({ length: Math.floor(rnd() * 30) }, () => `l${Math.floor(rnd() * 8)}`);
      const after = before.filter(() => rnd() > 0.3);
      for (let i = 0; i < rnd() * 6; i += 1) after.splice(Math.floor(rnd() * (after.length + 1)), 0, `n${Math.floor(rnd() * 5)}`);
      const lines = parseDiffLines(before.join("\n"), after.join("\n"));
      expect(rebuild(lines)).toEqual({ before, after });
    }
  });

  it("diffs an 8,000-line file with over a thousand changed lines quickly", () => {
    const before = Array.from({ length: 8000 }, (_, i) => `    "pkg-${i}": { "version": "1.0.${i}" },`);
    const after = before.map((line, i) => (i % 7 === 0 ? line.replace("1.0.", "2.1.") : line));
    const started = performance.now();
    const lines = parseDiffLines(before.join("\n"), after.join("\n"));
    expect(performance.now() - started).toBeLessThan(500);
    expect(lines.filter((l) => l.type === "add")).toHaveLength(1143);
    expect(rebuild(lines)).toEqual({ before, after });
  });
});
