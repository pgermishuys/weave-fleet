import { describe, expect, it } from "vitest";
import { foldTurns, summaryParts } from "@/lib/turn-fold";
import type { ActivityMessage } from "@/composables/use-activity-messages";

function msg(over: Partial<ActivityMessage> & { id: string; role: ActivityMessage["role"] }): ActivityMessage {
  return { author: "", senderKey: over.role, body: "", images: [], clusterPosition: "single", showIdentity: true, ...over } as ActivityMessage;
}
const tool = (id: string, kind: string, extra: Record<string, unknown> = {}) => ({ id, title: id, kind, status: "Completed", ...extra });
const edit = (id: string, path: string, adds: number, removes: number) => tool(id, "edit", {
  filePath: path,
  diffLines: [
    ...Array.from({ length: adds }, () => ({ type: "add", content: "+x" })),
    ...Array.from({ length: removes }, () => ({ type: "remove", content: "-x" })),
  ],
});
const open = new Set<string>();

function turn() {
  return [
    msg({ id: "u1", role: "user", body: "do it", createdAt: 1000 }),
    msg({ id: "a1", role: "assistant", createdAt: 2000, completedAt: 5000, reasoningParts: [{ partId: "r", type: "reasoning", text: "hmm" }], tools: [tool("t1", "read"), tool("t2", "read"), edit("t3", "/work/demo/src/notes.ts", 11, 2)] as never }),
    msg({ id: "a2", role: "assistant", createdAt: 6000, completedAt: 35_000, tools: [tool("t4", "bash")] as never }),
    msg({ id: "a3", role: "assistant", createdAt: 36_000, completedAt: 36_000, body: "All done." }),
  ];
}

describe("foldTurns", () => {
  it("folds a finished turn: tool rows and thinking go, the answer stays, the fold row and card appear", () => {
    const out = foldTurns(turn(), { running: false, directory: "/work/demo", open });
    expect(out.map((m) => m.id)).toEqual(["u1", "a1", "a3"]);
    const host = out[1]!;
    expect(host.foldOnly).toBe(true);
    expect(host.tools).toEqual([]);
    expect(host.fold?.summary).toMatchObject({ reads: 2, edits: 1, commands: 1, durationMs: 35_000 });
    expect(out[2]!.card?.files).toEqual([{ path: "src/notes.ts", name: "notes.ts", dir: "src", additions: 11, deletions: 2 }]);
    expect(summaryParts(host.fold!.summary)).toEqual(["read 2 files", "edited 1", "ran 1 command"]);
  });

  it("folds text written before or between calls, and keeps only the text after the last call", () => {
    const m = turn();
    m[1] = { ...m[1]!, body: "I'll start by looking." } as ActivityMessage;
    m[2] = { ...m[2]!, body: "Now the test." } as ActivityMessage;
    const out = foldTurns(m, { running: false, directory: "/work/demo", open });
    expect(out.map((x) => x.id)).toEqual(["u1", "a1", "a3"]);
    expect(out[1]!.body).toBe("");
    expect(out[2]!.body).toBe("All done.");
    // Opened, the narration shows where it was written.
    const opened = foldTurns(m, { running: false, directory: "/work/demo", open: new Set(["u1"]) });
    expect(opened[1]!.body).toBe("I'll start by looking.");
  });

  it("keeps the last text as the answer when all of it came before the last call", () => {
    const m = [
      msg({ id: "u", role: "user" }),
      msg({ id: "a", role: "assistant", body: "Here is the result.", tools: [tool("t", "bash")] as never }),
    ];
    expect(foldTurns(m, { running: false, open })[1]!.body).toBe("Here is the result.");
  });

  it("leaves the running turn open", () => {
    const out = foldTurns(turn(), { running: true, open });
    expect(out.every((m) => !m.fold && !m.foldOnly)).toBe(true);
    expect(out).toHaveLength(4);
  });

  it("folds every finished turn, the latest one included, but only the running one stays open", () => {
    const two = [...turn(), msg({ id: "u2", role: "user", createdAt: 40_000 }), msg({ id: "b1", role: "assistant", tools: [tool("t9", "read")] as never })];
    const out = foldTurns(two, { running: true, open });
    expect(out.find((m) => m.id === "a1")?.foldOnly).toBe(true);
    expect(out.find((m) => m.id === "b1")?.tools).toHaveLength(1);
  });

  it("keeps failed rows visible and counts them", () => {
    const m = turn();
    m[2] = msg({ id: "a2", role: "assistant", tools: [tool("t4", "bash", { status: "Error" })] as never });
    const out = foldTurns(m, { running: false, open });
    expect(out.find((x) => x.id === "a2")?.tools).toHaveLength(1);
    expect(out[1]!.fold?.summary.failed).toBe(1);
  });

  it("keeps sub-agent rows, pages and screenshots visible", () => {
    const m = [msg({ id: "u", role: "user" }), msg({ id: "a", role: "assistant", body: "x", tools: [tool("d", "task", { delegation: {} }), tool("p", "fleet_page_show", { page: {} }), tool("r", "read")] as never })];
    const out = foldTurns(m, { running: false, open });
    expect(out[1]!.tools?.map((t) => t.id)).toEqual(["d", "p"]);
  });

  it("shows everything again when the turn is opened, with the fold row and card kept", () => {
    const out = foldTurns(turn(), { running: false, directory: "/work/demo", open: new Set(["u1"]) });
    expect(out.map((m) => m.id)).toEqual(["u1", "a1", "a2", "a3"]);
    expect(out[1]!.fold).toBeDefined();
    expect(out[1]!.foldOnly).toBeUndefined();
    expect(out[3]!.card).toBeDefined();
  });

  it("leaves a turn with nothing to fold and no files as it is", () => {
    const m = [msg({ id: "u", role: "user" }), msg({ id: "a", role: "assistant", body: "hi" })];
    const out = foldTurns(m, { running: false, open });
    expect(out[1]).toBe(m[1]);
  });

  it("merges several edits of one file and shows a changed-files card even when nothing folds", () => {
    const m = [msg({ id: "u", role: "user" }), msg({ id: "a", role: "assistant", body: "ok", tools: [edit("e1", "a.ts", 2, 1), edit("e2", "a.ts", 3, 0)] as never })];
    const out = foldTurns(m, { running: false, open });
    expect(out[1]!.card).toMatchObject({ additions: 5, deletions: 1, files: [{ path: "a.ts", additions: 5, deletions: 1 }] });
  });
});
