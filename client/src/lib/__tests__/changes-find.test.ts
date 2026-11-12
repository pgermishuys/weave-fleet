import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { parseDiffLines } from "@/lib/diff-parser";
import { buildReview } from "@/lib/diff-review";
import {
  CHANGES_CURRENT,
  CHANGES_HIT,
  clearChangesMarks,
  findChangesHits,
  findLines,
  markChangesMatches,
  selectedQuery,
  stepShortcut,
  type FindFile,
} from "@/lib/changes-find";

const numbered = (n: number) => Array.from({ length: n }, (_, i) => `line ${i + 1}`);

function key(init: KeyboardEventInit): KeyboardEvent {
  return new KeyboardEvent("keydown", init);
}

describe("findLines", () => {
  it("lists a file's hunk and folded lines in drawn order, saying which are folded", () => {
    const before = numbered(30).join("\n");
    const after = before.replace("line 3\n", "line 3!\n").replace("line 28\n", "line 28!\n");
    const items = buildReview(parseDiffLines(before, after));
    const lines = findLines(items);

    expect(lines.map((line) => line.index)).toEqual([...lines.map((line) => line.index)].sort((a, b) => a - b));
    const folded = lines.filter((line) => line.inFold);
    expect(folded.length).toBe(18);
    expect(folded.every((line) => line.itemId === items[1]!.id)).toBe(true);
    expect(lines.find((line) => line.text === "line 3!")?.inFold).toBe(false);
  });
});

describe("findChangesHits", () => {
  const files: FindFile[] = [
    { path: "a.ts", lines: [{ index: 0, text: "const Note = note(note);", itemId: "h0", inFold: false }] },
    { path: "b.ts", lines: [{ index: 4, text: "nothing", itemId: "f1", inFold: true }, { index: 5, text: "NOTE", itemId: "f1", inFold: true }] },
  ];

  it("finds every match, ignoring case, file by file and left to right", () => {
    const hits = findChangesHits(files, "note");
    expect(hits.map((hit) => [hit.path, hit.line, hit.ordinal])).toEqual([
      ["a.ts", 0, 0],
      ["a.ts", 0, 1],
      ["a.ts", 0, 2],
      ["b.ts", 5, 0],
    ]);
    expect(hits[3]).toMatchObject({ itemId: "f1", inFold: true });
  });

  it("finds nothing for an empty or blank query", () => {
    expect(findChangesHits(files, "")).toEqual([]);
    expect(findChangesHits(files, "   ")).toEqual([]);
  });

  it("does not count overlapping matches twice", () => {
    expect(findChangesHits([{ path: "x", lines: [{ index: 0, text: "aaaa", itemId: "h", inFold: false }] }], "aa")).toHaveLength(2);
  });
});

describe("stepShortcut", () => {
  it("steps forward on F3 and Ctrl+G, back with Shift", () => {
    expect(stepShortcut(key({ key: "F3" }), false)).toBe(1);
    expect(stepShortcut(key({ key: "F3", shiftKey: true }), false)).toBe(-1);
    expect(stepShortcut(key({ key: "g", ctrlKey: true }), false)).toBe(1);
    expect(stepShortcut(key({ key: "G", ctrlKey: true, shiftKey: true }), false)).toBe(-1);
  });

  it("uses ⌘ on a Mac and ignores other keys", () => {
    expect(stepShortcut(key({ key: "g", metaKey: true }), true)).toBe(1);
    expect(stepShortcut(key({ key: "g", ctrlKey: true }), true)).toBe(0);
    expect(stepShortcut(key({ key: "g" }), false)).toBe(0);
    expect(stepShortcut(key({ key: "g", ctrlKey: true, altKey: true }), false)).toBe(0);
  });
});

describe("selectedQuery", () => {
  afterEach(() => {
    window.getSelection()?.removeAllRanges();
    document.body.innerHTML = "";
  });

  it("takes a one-line selection inside the root, and nothing from outside it", () => {
    document.body.innerHTML = `<div id="root"><span id="in">const title = 1;</span></div><span id="out">elsewhere</span>`;
    const root = document.getElementById("root")!;
    const select = (id: string, from: number, to: number) => {
      const text = document.getElementById(id)!.firstChild!;
      const range = document.createRange();
      range.setStart(text, from);
      range.setEnd(text, to);
      window.getSelection()!.removeAllRanges();
      window.getSelection()!.addRange(range);
    };

    select("in", 6, 11);
    expect(selectedQuery(root)).toBe("title");
    select("out", 0, 4);
    expect(selectedQuery(root)).toBe("");
  });
});

describe("markChangesMatches", () => {
  const set = new Map<string, Range[]>();
  beforeEach(() => {
    set.clear();
    (globalThis as { CSS?: unknown }).CSS = {
      highlights: { set: (name: string, value: { ranges: Range[] }) => set.set(name, value.ranges), delete: (name: string) => set.delete(name) },
    };
    (globalThis as { Highlight?: unknown }).Highlight = class {
      ranges: Range[];
      constructor(...ranges: Range[]) {
        this.ranges = ranges;
      }
    };
  });
  afterEach(() => {
    delete (globalThis as { CSS?: unknown }).CSS;
    delete (globalThis as { Highlight?: unknown }).Highlight;
    document.body.innerHTML = "";
  });

  it("marks both copies of a line split view draws twice, and the current match on each", () => {
    document.body.innerHTML = `
      <div id="body">
        <section data-review-file="a.ts">
          <span data-find-line="3">tag and <mark>tag</mark></span>
          <span data-find-line="3">tag and <mark>tag</mark></span>
          <span data-find-line="4">no match here</span>
        </section>
        <section data-review-file="b.ts"><span data-find-line="3">tag</span></section>
      </div>`;
    const body = document.getElementById("body")!;
    const hits = findChangesHits(
      [
        { path: "a.ts", lines: [{ index: 3, text: "tag and tag", itemId: "h", inFold: false }] },
        { path: "b.ts", lines: [{ index: 3, text: "tag", itemId: "h", inFold: false }] },
      ],
      "TAG",
    );

    const range = markChangesMatches(body, hits, "TAG", hits[1]!);

    expect(set.get(CHANGES_HIT)).toHaveLength(5);
    const current = set.get(CHANGES_CURRENT)!;
    expect(current).toHaveLength(2);
    expect(current.every((r) => r.startContainer.parentElement?.tagName === "MARK")).toBe(true);
    expect(range).toBe(current[0]);

    clearChangesMarks();
    expect(set.size).toBe(0);
  });

  it("returns no current range when the current match isn't drawn", () => {
    document.body.innerHTML = `<div id="body"><section data-review-file="a.ts"></section></div>`;
    const hits = findChangesHits([{ path: "a.ts", lines: [{ index: 9, text: "tag", itemId: "f", inFold: true }] }], "tag");
    expect(markChangesMatches(document.getElementById("body")!, hits, "tag", hits[0]!)).toBeNull();
    expect(set.has(CHANGES_CURRENT)).toBe(false);
  });
});
