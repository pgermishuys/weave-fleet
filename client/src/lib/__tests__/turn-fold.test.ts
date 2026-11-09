import { describe, expect, it } from "vitest";
import { capturableStats, foldTurns, relativePath, summaryParts, withFileStats } from "@/lib/turn-fold";
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

  it("never hides the agent's text: narration between calls stays, in order, and only tool rows fold", () => {
    const m = turn();
    m[1] = { ...m[1]!, body: "I'll start by looking." } as ActivityMessage;
    m[2] = { ...m[2]!, body: "Now the test." } as ActivityMessage;
    const out = foldTurns(m, { running: false, directory: "/work/demo", open });
    expect(out.map((x) => x.id)).toEqual(["u1", "a1", "a2", "a3"]);
    expect(out.map((x) => x.body)).toEqual(["do it", "I'll start by looking.", "Now the test.", "All done."]);
    expect(out[1]!.foldOnly).toBeUndefined();
    expect(out[1]!.fold).toBeDefined();
    expect(out[1]!.tools).toEqual([]);
  });

  it("keeps the reply to a steered message visible in its own turn", () => {
    const m = [
      msg({ id: "u1", role: "user", createdAt: 1 }),
      msg({ id: "a1", role: "assistant", tools: [tool("t", "bash")] as never }),
      msg({ id: "u2", role: "user", body: "also this", createdAt: 5 }),
      msg({ id: "a2", role: "assistant", body: "Sure, done.", tools: [tool("t2", "read")] as never }),
    ];
    const out = foldTurns(m, { running: false, open });
    expect(out.find((x) => x.id === "a2")!.body).toBe("Sure, done.");
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

describe("whole-file writes", () => {
  // A Write sends only the new contents: its lines are all "added" (28), but the file changed by +11 −2.
  const write = (id: string, path: string, lines: number) => tool(id, "write", {
    filePath: path,
    diffFromContent: true,
    diffLines: Array.from({ length: lines }, () => ({ type: "add", content: "+x" })),
  });
  const writeTurn = () => [
    msg({ id: "u1", role: "user", body: "do it", createdAt: 1000 }),
    msg({ id: "a1", role: "assistant", createdAt: 2000, completedAt: 5000, tools: [tool("t1", "read"), write("t2", "/work/demo/src/notes.ts", 28), write("t3", "/work/demo/test/notes.test.ts", 25)] as never }),
    msg({ id: "a2", role: "assistant", createdAt: 6000, completedAt: 6000, body: "Done." }),
  ];
  const stats = new Map([
    ["src/notes.ts", { additions: 11, deletions: 2 }],
    ["test/notes.test.ts", { additions: 14, deletions: 1 }],
  ]);

  it("counts the file's real change from the session diff, not every line written", () => {
    const messages = withFileStats(writeTurn(), stats, "/work/demo");
    const card = foldTurns(messages, { running: false, directory: "/work/demo", open }).at(-1)!.card!;
    expect(card.files.map((f) => [f.path, f.additions, f.deletions])).toEqual([["src/notes.ts", 11, 2], ["test/notes.test.ts", 14, 1]]);
    expect([card.additions, card.deletions, card.countsKnown]).toEqual([25, 3, true]);
    expect(messages[1]!.tools![1]).toMatchObject({ diffStat: { additions: 11, deletions: 2 } });
  });

  it("draws no counts while the session diff isn't loaded, rather than a wrong +28", () => {
    const messages = withFileStats(writeTurn(), null, "/work/demo");
    const card = foldTurns(messages, { running: false, directory: "/work/demo", open }).at(-1)!.card!;
    expect(card.files.every((f) => f.unknownCounts)).toBe(true);
    expect(card.countsKnown).toBe(false);
  });

  it("counts a file the session diff doesn't list as unchanged", () => {
    const messages = withFileStats(writeTurn(), new Map(), "/work/demo");
    const card = foldTurns(messages, { running: false, directory: "/work/demo", open }).at(-1)!.card!;
    expect([card.additions, card.deletions]).toEqual([0, 0]);
  });

  it("does not add up a file written twice: the file's change stands once", () => {
    const m = writeTurn();
    m[1] = { ...m[1]!, tools: [write("t2", "/work/demo/src/notes.ts", 28), write("t3", "/work/demo/src/notes.ts", 28)] as never } as ActivityMessage;
    const card = foldTurns(withFileStats(m, stats, "/work/demo"), { running: false, directory: "/work/demo", open }).at(-1)!.card!;
    expect(card.files).toHaveLength(1);
    expect([card.files[0]!.additions, card.files[0]!.deletions]).toEqual([11, 2]);
  });
});


describe("regressions in the folded turn", () => {
  it("keeps a Show page row out of the fold even when the call carries no page, and doesn't count it", () => {
    const m = [msg({ id: "u", role: "user" }), msg({ id: "a", role: "assistant", tools: [tool("p", "mcp__fleet__fleet_page_show"), tool("r", "read")] as never })];
    const out = foldTurns(m, { running: false, open });
    expect(out[1]!.tools?.map((t) => t.id)).toEqual(["p"]);
    expect(out[1]!.fold?.summary).toMatchObject({ other: 0, reads: 1 });
    expect(summaryParts(out[1]!.fold!.summary)).toEqual(["read 1 file"]);
  });

  it("doesn't count ExitPlanMode as a read", () => {
    const m = [msg({ id: "u", role: "user" }), msg({ id: "a", role: "assistant", tools: [tool("x", "ExitPlanMode")] as never })];
    const summary = foldTurns(m, { running: false, open })[1]!.fold!.summary;
    expect(summary.reads).toBe(0);
    expect(summary.edits).toBe(0);
  });

  it("marks a turn that failed or was stopped, so no ✓ is drawn for it", () => {
    const failed = [msg({ id: "u", role: "user" }), msg({ id: "a", role: "assistant", tools: [tool("t", "read")] as never }), msg({ id: "b", role: "assistant", turnError: { name: "x", message: "boom", isRetryable: false } })];
    expect(foldTurns(failed, { running: false, open })[1]!.fold!.summary.outcome).toBe("failed");
    const stopped = [msg({ id: "u", role: "user" }), msg({ id: "a", role: "assistant", tools: [tool("r", "read"), tool("t", "bash", { status: "Stopped" })] as never })];
    expect(foldTurns(stopped, { running: false, open })[1]!.fold!.summary.outcome).toBe("stopped");
    expect(foldTurns(turn(), { running: false, open })[1]!.fold!.summary.outcome).toBe("done");
  });

  it("ends 'Worked for' with the turn's own work, not a later background notice or retry", () => {
    const m = [
      msg({ id: "u", role: "user", createdAt: 1000 }),
      msg({ id: "a", role: "assistant", createdAt: 1500, completedAt: 2000, tools: [tool("t", "bash")] as never }),
      msg({ id: "n", role: "assistant", createdAt: 21_000, completedAt: 21_000, background: { state: "completed" } as never }),
      msg({ id: "r", role: "assistant", createdAt: 22_000, completedAt: 23_000, body: "Picked it up." }),
    ];
    expect(foldTurns(m, { running: false, open })[1]!.fold!.summary.durationMs).toBe(1000);
    const retry = [
      msg({ id: "u", role: "user", createdAt: 1000 }),
      msg({ id: "a", role: "assistant", createdAt: 1500, completedAt: 2000, turnError: { name: "x", message: "m", isRetryable: true }, tools: [tool("t", "read")] as never }),
      msg({ id: "b", role: "assistant", createdAt: 90_000, completedAt: 95_000, body: "later" }),
    ];
    expect(foldTurns(retry, { running: false, open })[1]!.fold!.summary.durationMs).toBe(1000);
  });

  it("relativises Windows paths (either slash, any drive case) so the card finds their stats", () => {
    expect(relativePath("C:\\work\\demo\\src\\a.ts", "c:\\work\\demo")).toBe("src/a.ts");
    expect(relativePath("/work/demo/src/a.ts", "/work/demo/")).toBe("src/a.ts");
    expect(relativePath("/elsewhere/a.ts", "/work/demo")).toBe("/elsewhere/a.ts");
  });

  describe("past turns keep what they changed", () => {
    const write = (id: string, path: string) => tool(id, "write", { filePath: path, diffFromContent: true, diffLines: [{ type: "add", content: "+x" }] });
    const two = () => [
      msg({ id: "u1", role: "user" }), msg({ id: "a1", role: "assistant", body: "ok", tools: [write("w1", "/work/demo/README.md")] as never }),
      msg({ id: "u2", role: "user" }), msg({ id: "a2", role: "assistant", body: "ok" }),
    ];
    const cardOf = (messages: ActivityMessage[]) => foldTurns(messages, { running: false, directory: "/work/demo", open }).find((x) => x.id === "a1")!.card!;

    it("doesn't recount an earlier turn from today's diff: unknown draws no counts", () => {
      const today = new Map([["README.md", { additions: 0, deletions: 3 }]]);
      const card = cardOf(withFileStats(two(), today, "/work/demo"));
      expect(card.countsKnown).toBe(false);
    });

    it("uses the counts kept when the turn ended", () => {
      const today = new Map([["README.md", { additions: 0, deletions: 3 }]]);
      const card = cardOf(withFileStats(two(), today, "/work/demo", new Map([["w1", { additions: 3, deletions: 1 }]])));
      expect([card.additions, card.deletions]).toEqual([3, 1]);
    });

    it("captures only the latest turn's writes", () => {
      const stats = new Map([["README.md", { additions: 3, deletions: 1 }]]);
      expect(capturableStats(two(), stats, "/work/demo")).toEqual([]);
      expect(capturableStats(two().slice(0, 2), stats, "/work/demo")).toEqual([["w1", { additions: 3, deletions: 1 }]]);
    });

    it("a file outside the session's folder is unknown, not +0 −0", () => {
      const m = two().slice(0, 2);
      m[1] = { ...m[1]!, tools: [write("w9", "/other/place/x.md")] as never } as ActivityMessage;
      const card = foldTurns(withFileStats(m, new Map(), "/work/demo"), { running: false, directory: "/work/demo", open }).at(-1)!.card!;
      expect(card.countsKnown).toBe(false);
    });
  });
});
