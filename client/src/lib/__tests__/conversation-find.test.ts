import { describe, expect, it } from "vitest";
import {
  countOccurrences,
  findHits,
  focusKeepsFindKey,
  isFindShortcut,
  rangesIn,
  type FindUnit,
} from "@/lib/conversation-find";
import { turnIdsOf } from "@/lib/turn-fold";
import type { ActivityMessage } from "@/composables/use-activity-messages";

const unit = (id: string, text: string, toolText: string[] = [], turnId: string | null = null): FindUnit => ({ id, text, toolText, turnId });

describe("countOccurrences", () => {
  it("ignores case and doesn't overlap", () => {
    expect(countOccurrences("Retry retry RETRY", "retry")).toBe(3);
    expect(countOccurrences("aaaa", "aa")).toBe(2);
    expect(countOccurrences("nothing", "retry")).toBe(0);
    expect(countOccurrences("anything", "")).toBe(0);
  });
});

describe("findHits", () => {
  const units = [
    unit("m1", "Please retry the build", [], "t1"),
    unit("m2", "Retrying now; retry 2", ["retry failed: exit 1"], "t1"),
    unit("m3", "done"),
  ];

  it("counts messages only by default, oldest first", () => {
    const hits = findHits(units, "retry", "messages");
    expect(hits.map((hit) => [hit.unitId, hit.ordinal])).toEqual([["m1", 0], ["m2", 0], ["m2", 1]]);
  });

  it("adds tool output when asked, after the message's own text, and says where it is", () => {
    const hits = findHits(units, "retry", "tools");
    expect(hits).toHaveLength(4);
    expect(hits[3]).toMatchObject({ unitId: "m2", ordinal: 2, kind: "tool", turnId: "t1" });
  });

  it("finds nothing for an empty query", () => {
    expect(findHits(units, "  ", "messages")).toEqual([]);
  });
});

describe("Ctrl/⌘F focus rules", () => {
  it("lets fields, editors, the terminal and dialogs keep the key", () => {
    document.body.innerHTML = `<textarea id="a"></textarea><input id="b"><textarea id="g" class="composer-frame__textarea"></textarea><div class="cm-editor"><div id="c" tabindex="0"></div></div>
      <div class="xterm"><span id="d"></span></div><div role="dialog"><button id="e"></button></div><p id="f">text</p>`;
    for (const id of ["a", "b", "c", "d", "e"]) expect(focusKeepsFindKey(document.getElementById(id))).toBe(true);
    expect(focusKeepsFindKey(document.getElementById("f"))).toBe(false);
    // The composer has no Ctrl/⌘F of its own: it counts as part of the conversation.
    expect(focusKeepsFindKey(document.getElementById("g"))).toBe(false);
    expect(focusKeepsFindKey(document.body)).toBe(false);
  });

  it("matches Ctrl+F, or ⌘F on a Mac, and nothing with extra modifiers", () => {
    const key = (init: KeyboardEventInit) => new KeyboardEvent("keydown", { key: "f", ...init });
    expect(isFindShortcut(key({ ctrlKey: true }), false)).toBe(true);
    expect(isFindShortcut(key({ metaKey: true }), false)).toBe(false);
    expect(isFindShortcut(key({ metaKey: true }), true)).toBe(true);
    expect(isFindShortcut(key({ ctrlKey: true, shiftKey: true }), false)).toBe(false);
    expect(isFindShortcut(key({ ctrlKey: true, altKey: true }), false)).toBe(false);
  });
});

describe("rangesIn", () => {
  it("finds a match that runs across inline elements and skips buttons", () => {
    const root = document.createElement("div");
    root.innerHTML = "<p>a re<b>try</b> and Retry</p><button>retry</button>";
    const ranges = rangesIn(root, "retry");
    expect(ranges.map((range) => range.toString())).toEqual(["retry", "Retry"]);
  });
});

describe("turnIdsOf", () => {
  const message = (id: string, role: ActivityMessage["role"]) => ({ id, role }) as ActivityMessage;
  it("names a turn by its prompt, for the prompt and every agent message after it", () => {
    const ids = turnIdsOf([message("u1", "user"), message("a1", "assistant"), message("a2", "assistant"), message("u2", "user")]);
    expect(ids.get("a2")).toBe("u1");
    expect(ids.get("u1")).toBe("u1");
    expect(ids.has("u2")).toBe(false);
  });
});
