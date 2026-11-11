import { describe, expect, it } from "vitest";
import { mount } from "@vue/test-utils";
import TurnFoldRow from "@/components/session/TurnFoldRow.vue";
import type { FoldedTurn } from "@/lib/turn-fold";

const turn: FoldedTurn = {
  id: "u1",
  summary: { durationMs: 34_000, reads: 4, edits: 2, commands: 2, other: 0, failed: 0, outcome: "done" },
};

describe("TurnFoldRow", () => {
  it("says what the turn did on one line and toggles on click", async () => {
    const w = mount(TurnFoldRow, { props: { turn, open: false } });
    expect(w.text()).toContain("Worked for 34s");
    expect(w.text()).toContain("read 4 files · edited 2 · ran 2 commands");
    expect(w.attributes("aria-expanded")).toBe("false");
    await w.trigger("click");
    expect(w.emitted("toggle")).toHaveLength(1);
  });
});

