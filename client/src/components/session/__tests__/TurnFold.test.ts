import { describe, expect, it } from "vitest";
import { mount } from "@vue/test-utils";
import TurnFoldRow from "@/components/session/TurnFoldRow.vue";
import ChangedFilesCard from "@/components/session/ChangedFilesCard.vue";
import type { FoldedTurn } from "@/lib/turn-fold";

const turn: FoldedTurn = {
  id: "u1",
  summary: { durationMs: 34_000, reads: 4, edits: 2, commands: 2, other: 0, failed: 0, outcome: "done" },
  files: [
    { path: "src/notes.ts", name: "notes.ts", dir: "src", additions: 11, deletions: 2 },
    { path: "test/notes.test.ts", name: "notes.test.ts", dir: "test", additions: 14, deletions: 1 },
  ],
  additions: 25,
  deletions: 3,
  countsKnown: true,
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

describe("ChangedFilesCard", () => {
  it("shows the totals, a row per file, and opens the diff or one file", async () => {
    const w = mount(ChangedFilesCard, { props: { turn } });
    expect(w.text()).toContain("2 files changed");
    expect(w.text()).toContain("+25");
    expect(w.findAll("[data-testid='changed-files-row']")).toHaveLength(2);
    await w.findAll("[data-testid='changed-files-row']")[1]!.trigger("click");
    expect(w.emitted("open-file")![0]).toEqual(["test/notes.test.ts"]);
    await w.get("[data-testid='changed-files-open-diff']").trigger("click");
    expect(w.emitted("open-diff")).toHaveLength(1);
  });
});
