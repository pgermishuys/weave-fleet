import { beforeEach, describe, expect, it } from "vitest";
import {
  addDraftTerminalContext,
  clearDraftTerminalContext,
  useDraftTerminalContext,
} from "@/composables/use-draft-terminal-context";

const lines = { terminalId: "t1", label: "zsh", from: 9, to: 11, text: "FAIL" };

describe("useDraftTerminalContext", () => {
  beforeEach(() => {
    clearDraftTerminalContext("s1");
    clearDraftTerminalContext("s2");
  });

  it("keeps each session's lines apart, and survives a new composable", () => {
    addDraftTerminalContext("s1", lines);

    expect(useDraftTerminalContext("s1").contexts.value.map((c) => c.label)).toEqual(["zsh"]);
    expect(useDraftTerminalContext("s2").contexts.value).toEqual([]);
  });

  it("adds the same lines of the same terminal once", () => {
    addDraftTerminalContext("s1", lines);
    addDraftTerminalContext("s1", { ...lines, text: "FAIL again" });
    addDraftTerminalContext("s1", { ...lines, from: 12, to: 12 });

    expect(useDraftTerminalContext("s1").contexts.value).toHaveLength(2);
  });

  it("removes one, or clears them all", () => {
    addDraftTerminalContext("s1", lines);
    addDraftTerminalContext("s1", { ...lines, from: 12, to: 12 });
    const draft = useDraftTerminalContext("s1");

    draft.removeContext(draft.contexts.value[0].id);
    expect(draft.contexts.value.map((c) => c.from)).toEqual([12]);

    draft.clearContexts();
    expect(draft.contexts.value).toEqual([]);
  });
});
