import { describe, expect, it } from "vitest";
import type { SessionListItem } from "@/api/client";
import { buildTiles, groupTiles, needsYou, summarize, type MissionMachine } from "@/lib/mission-control";
import type { InboxAsk } from "@/lib/phone/inbox";

const NOW = Date.parse("2026-10-27T12:00:00Z");
const minutesAgo = (minutes: number) => NOW - minutes * 60_000;

function session(id: string, patch: Partial<SessionListItem> & { updated?: number; title?: string } = {}): SessionListItem {
  const { updated = minutesAgo(1), title = id, ...rest } = patch;
  return {
    session: { id, title, time: { created: updated - 1000, updated }, tags: [] },
    instanceId: "i",
    workspaceId: "w",
    workspaceDirectory: `/work/${rest.projectName ?? "ledger-api"}`,
    workspaceDisplayName: null,
    isolationStrategy: "existing",
    sessionStatus: "active",
    activityStatus: "busy",
    retentionStatus: "active",
    lifecycleStatus: "running",
    typedInstanceStatus: "running",
    isHidden: false,
    harnessType: "opencode2",
    projectName: "ledger-api",
    tags: [],
    ...rest,
  } as unknown as SessionListItem;
}

function machine(id: string, name: string, sessions: SessionListItem[], extra: Partial<MissionMachine> = {}): MissionMachine {
  return { id, name, status: "live", sessions, asks: {}, now: {}, ...extra };
}

const question: InboxAsk = {
  kind: "question",
  requestId: "q1",
  question: { header: "Retries", question: "Retry three times or five?", options: [{ label: "Three", description: "" }] },
  more: 0,
};

describe("mission control tiles", () => {
  it("makes a tile for every machine's working and waiting session, newest first, and drops what's old, archived or a helper", () => {
    const tiles = buildTiles([
      machine("home", "atlas", [
        session("a-work", { updated: minutesAgo(2) }),
        session("a-old", { sessionStatus: "idle", activityStatus: "idle", updated: minutesAgo(60 * 30) }),
        session("a-archived", { retentionStatus: "archived" }),
        session("a-helper", { parentSessionId: "a-work" }),
      ]),
      machine("m2", "mini", [session("m-wait", { sessionStatus: "waiting_input", updated: minutesAgo(1) })]),
    ], NOW);

    expect(tiles.map((tile) => tile.key)).toEqual(["m2:m-wait", "home:a-work"]);
    expect(tiles.map((tile) => tile.machineName)).toEqual(["mini", "atlas"]);
  });

  it("shows the machine's now line while working, the question while it waits, and the state when it knows nothing", () => {
    const tiles = buildTiles([
      machine("home", "atlas", [
        session("working", { updated: minutesAgo(1) }),
        session("waiting", { sessionStatus: "waiting_input", updated: minutesAgo(2) }),
        session("quiet-machine", { updated: minutesAgo(3) }),
        session("stopped", { sessionStatus: "error", updated: minutesAgo(4) }),
      ], {
        now: { working: { text: "bash · pnpm test webhooks", kind: "tool", at: "" } },
        asks: { waiting: question },
      }),
    ], NOW);

    const by = Object.fromEntries(tiles.map((tile) => [tile.sessionId, tile.now]));
    expect(by).toEqual({
      working: "bash · pnpm test webhooks",
      waiting: "? Retry three times or five?",
      "quiet-machine": "Working",
      stopped: "Stopped with an error",
    });
  });

  it("names the harness, the model, the todos and the helpers", () => {
    const [tile] = buildTiles([machine("home", "atlas", [
      session("s", {
        harnessType: "claude-code",
        selectedModel: { providerID: "anthropic", modelID: "sonnet-5.5" },
        progress: { sessionId: "s", kind: "todos", done: 3, total: 7 },
        runningWorkCount: 2,
      }),
    ])], NOW);

    expect(tile).toMatchObject({ harness: "Claude Code", model: "sonnet-5.5", todos: { done: 3, total: 7 }, helpers: 2 });
  });

  it("marks tiles of a machine that stopped answering", () => {
    const [tile] = buildTiles([machine("m2", "mini", [session("s")], { status: "unreachable" })], NOW);

    expect(tile.stale).toBe(true);
  });
});

describe("grouping and what needs you", () => {
  const tiles = buildTiles([
    machine("home", "atlas", [
      session("a1", { projectName: "ledger-api", updated: minutesAgo(1) }),
      session("a2", { projectName: "docs-site", sessionStatus: "waiting_input", updated: minutesAgo(2) }),
    ], { asks: { a2: question } }),
    machine("m2", "mini", [
      session("m1", { projectName: "ledger-api", updated: minutesAgo(3), progress: { sessionId: "m1", kind: "todos", done: 1, total: 2 }, runningWorkCount: 2 }),
      session("m2", { projectName: "ledger-ios", sessionStatus: "error", updated: minutesAgo(4) }),
    ]),
  ], NOW);

  it("groups by folder across machines, alphabetically", () => {
    const groups = groupTiles(tiles, "folder");

    expect(groups.map((group) => [group.label, group.tiles.map((tile) => tile.sessionId)])).toEqual([
      ["docs-site", ["a2"]],
      ["ledger-api", ["a1", "m1"]],
      ["ledger-ios", ["m2"]],
    ]);
  });

  it("groups by machine, and by state with what needs you first", () => {
    expect(groupTiles(tiles, "machine").map((group) => [group.label, group.tiles.length])).toEqual([["atlas", 2], ["mini", 2]]);
    expect(groupTiles(tiles, "state").map((group) => group.label)).toEqual(["Needs you", "Stopped with an error", "Working"]);
  });

  it("lists what waits on the user and counts what runs", () => {
    expect(needsYou(tiles).map((tile) => tile.sessionId)).toEqual(["a2", "m2"]);
    expect(summarize(tiles)).toEqual({ running: 2, helpers: 2, needYou: 2 });
  });
});
