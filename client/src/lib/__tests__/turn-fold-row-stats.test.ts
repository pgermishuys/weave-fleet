import { describe, expect, it } from "vitest";
import { withFileStats, type TurnStatsSnapshot } from "@/lib/turn-fold";
import type { ActivityMessage } from "@/composables/use-activity-messages";

function msg(over: Partial<ActivityMessage> & { id: string; role: ActivityMessage["role"] }): ActivityMessage {
  return { author: "", senderKey: over.role, body: "", images: [], clusterPosition: "single", showIdentity: true, ...over } as ActivityMessage;
}
const write = (id: string, path: string, lines: number) => ({
  id,
  title: id,
  kind: "write",
  status: "Completed",
  filePath: path,
  diffFromContent: true,
  diffLines: Array.from({ length: lines }, () => ({ type: "add", content: "+x" })),
});
const snap = (endedAt: number, files: Record<string, [number, number]>): TurnStatsSnapshot => ({
  endedAt,
  files: new Map(Object.entries(files).map(([path, [additions, deletions]]) => [path, { additions, deletions }])),
});

describe("a Write row's counts come from its own turn's snapshot", () => {
  // Two quick turns, as a fast model runs them: the second's prompt comes under two seconds after the first turn's
  // snapshot. The second writes a new file; only its own snapshot lists it.
  const turns = () => [
    msg({ id: "u1", role: "user", createdAt: 30_000 }),
    msg({ id: "a1", role: "assistant", createdAt: 31_000, completedAt: 34_500, tools: [write("w1", "/work/demo/src/notes.ts", 20)] as never }),
    msg({ id: "u2", role: "user", createdAt: 35_500 }),
    msg({ id: "a2", role: "assistant", createdAt: 36_000, completedAt: 36_600, tools: [write("w2", "/work/demo/src/archive.js", 3)] as never }),
  ];
  const snapshots = [
    snap(34_860, { "src/notes.ts": [1, 0] }),
    snap(37_000, { "src/notes.ts": [1, 0], "src/archive.js": [3, 0] }),
  ];

  it("counts a new file written in a turn that follows close behind the last one, not +0 −0 from the turn before", () => {
    const out = withFileStats(turns(), null, "/work/demo", snapshots);
    expect(out.find((m) => m.id === "a2")!.tools![0]).toMatchObject({ diffStat: { additions: 3, deletions: 0 } });
  });

  it("keeps the earlier turn on its own snapshot", () => {
    const out = withFileStats(turns(), null, "/work/demo", snapshots);
    expect(out.find((m) => m.id === "a1")!.tools![0]).toMatchObject({ diffStat: { additions: 1, deletions: 0 } });
  });
});
