import { describe, expect, it } from "vitest";
import { MATCHES_PER_FILE, segmentsOf, visibleHits, type TextFile } from "@/lib/find-text";

function file(path: string, lines: number[]): TextFile {
  return { path, total: lines.length, matches: lines.map((line) => ({ line, column: 1, text: "x", ranges: [] })) };
}

describe("segmentsOf", () => {
  it("splits_a_line_into_plain_and_marked_pieces", () => {
    expect(segmentsOf("const MaxAttempts = MaxAttempts;", [{ start: 6, end: 17 }, { start: 20, end: 31 }])).toEqual([
      { text: "const ", marked: false },
      { text: "MaxAttempts", marked: true },
      { text: " = ", marked: false },
      { text: "MaxAttempts", marked: true },
      { text: ";", marked: false },
    ]);
  });

  it("returns_the_whole_line_when_nothing_is_marked_and_ignores_ranges_outside_it", () => {
    expect(segmentsOf("hello", [])).toEqual([{ text: "hello", marked: false }]);
    expect(segmentsOf("hello", [{ start: 9, end: 12 }])).toEqual([{ text: "hello", marked: false }]);
  });

  it("keeps_overlapping_ranges_from_repeating_text", () => {
    expect(segmentsOf("abcdef", [{ start: 0, end: 4 }, { start: 2, end: 6 }]).map((part) => part.text).join("")).toBe("abcdef");
  });
});

describe("visibleHits", () => {
  it("shows_the_first_matches_per_file_until_the_file_is_expanded", () => {
    const files = [file("a.ts", [1, 2, 3, 4, 5, 6, 7]), file("b.ts", [9])];

    expect(visibleHits(files, new Set())).toHaveLength(MATCHES_PER_FILE + 1);
    expect(visibleHits(files, new Set(["a.ts"]))).toHaveLength(8);
    expect(visibleHits(files, new Set()).at(-1)).toEqual({ path: "b.ts", line: 9 });
  });
});
