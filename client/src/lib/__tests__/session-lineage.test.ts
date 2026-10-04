import { describe, expect, it } from "vitest";
import type { SessionListItem } from "@/api/client";
import type { AccumulatedMessage } from "@/lib/client-types";
import { toRunningWorkItem, type RunningWorkItem } from "@/lib/running-work";
import {
  buildAgentsLineage,
  lineageDescendants,
  lineageOf,
  nestLineage,
  runningSubagentsBySession,
  summarizeAgentActivity,
} from "@/lib/session-lineage";

function session(id: string, extra: Partial<SessionListItem> = {}): SessionListItem {
  return {
    instanceId: `i-${id}`, workspaceId: "w", workspaceDirectory: "/repo", workspaceDisplayName: null,
    isolationStrategy: "existing", sessionStatus: "idle", session: { id, title: `Session ${id}` } as SessionListItem["session"],
    instanceStatus: "running", lifecycleStatus: "running", retentionStatus: "active", typedInstanceStatus: "running",
    isHidden: false, tags: [], ...extra,
  };
}

/** Server-shaped work (nulls and false left out), as in the contract tests. */
function work(id: string, extra: Record<string, unknown> = {}): RunningWorkItem {
  return toRunningWorkItem({
    id, sessionId: "parent", workId: `call_${id}`, kind: "subagent", title: "code-reviewer", label: "Review the diff",
    status: "running", background: true, childSessionId: `child-${id}`, canStop: true, startedAt: "2026-10-04T10:00:00Z",
    ...extra,
  })!;
}

function ended(id: string, endedAt: string, extra: Record<string, unknown> = {}): RunningWorkItem {
  return work(id, { status: "completed", endedAt, endedReason: "completed", ...extra });
}

describe("lineageOf", () => {
  it("reads a fork, a started session and a subagent's hidden session", () => {
    expect(lineageOf({ forkedFromSessionId: "a", spawnKind: "fork" })).toEqual({ parentId: "a", kind: "fork" });
    expect(lineageOf({ spawnedBySessionId: "b", spawnKind: "api" })).toEqual({ parentId: "b", kind: "started" });
    expect(lineageOf({ parentSessionId: "c" })).toEqual({ parentId: "c", kind: "subagent" });
    expect(lineageOf({})).toBeNull();
  });

  it("lets spawnKind decide when a session is both a fork and started by an agent", () => {
    expect(lineageOf({ forkedFromSessionId: "a", spawnedBySessionId: "b", spawnKind: "fork" })?.parentId).toBe("a");
    expect(lineageOf({ forkedFromSessionId: "a", spawnedBySessionId: "b", spawnKind: "api" })?.parentId).toBe("b");
  });
});

describe("nestLineage", () => {
  it("nests forks and started sessions under their parent, in list order", () => {
    const list = [
      session("fork", { forkedFromSessionId: "p", spawnKind: "fork" }),
      session("p"),
      session("started", { spawnedBySessionId: "p", spawnKind: "api" }),
      session("other"),
    ];

    const { roots, childrenOf } = nestLineage(list);

    expect(roots.map((item) => item.session.id)).toEqual(["p", "other"]);
    expect(childrenOf.get("p")?.map(({ item, kind }) => [item.session.id, kind])).toEqual([["fork", "fork"], ["started", "started"]]);
  });

  it("keeps a child at the top when its parent isn't in the list", () => {
    const { roots, childrenOf } = nestLineage([session("fork", { forkedFromSessionId: "archived", spawnKind: "fork" })]);

    expect(roots.map((item) => item.session.id)).toEqual(["fork"]);
    expect(childrenOf.size).toBe(0);
  });

  it("nests a fork of a started session under that session, and lists descendants depth first", () => {
    const deep = nestLineage([
      session("p"),
      session("s1", { spawnedBySessionId: "p", spawnKind: "api" }),
      session("f1", { forkedFromSessionId: "s1", spawnKind: "fork" }),
      session("s2", { spawnedBySessionId: "p", spawnKind: "api" }),
    ]);

    expect(deep.roots.map((item) => item.session.id)).toEqual(["p"]);
    expect(deep.childrenOf.get("p")?.map(({ item }) => item.session.id)).toEqual(["s1", "s2"]);
    expect(deep.childrenOf.get("s1")?.map(({ item, kind }) => [item.session.id, kind])).toEqual([["f1", "fork"]]);
    expect(lineageDescendants(deep.roots[0]!, deep.childrenOf).map(({ item, depth }) => [item.session.id, depth])).toEqual([
      ["s1", 1],
      ["f1", 2],
      ["s2", 1],
    ]);
  });

  it("keeps every session in a loop of parents at the top", () => {
    const cycle = nestLineage([
      session("a", { forkedFromSessionId: "b", spawnKind: "fork" }),
      session("b", { forkedFromSessionId: "a", spawnKind: "fork" }),
      session("c", { forkedFromSessionId: "a", spawnKind: "fork" }),
    ]);

    expect(cycle.roots.map((item) => item.session.id)).toEqual(["a", "b"]);
    expect(cycle.childrenOf.get("a")?.map(({ item }) => item.session.id)).toEqual(["c"]);
  });
});

describe("runningSubagentsBySession", () => {
  it("keeps running subagents that have a session, by the session that started them", () => {
    const bySession = runningSubagentsBySession([
      work("a"),
      work("shell", { kind: "shell", childSessionId: undefined }),
      work("pi", { childSessionId: undefined }),
      ended("done", "2026-10-04T10:01:00Z"),
    ]);

    expect([...bySession.keys()]).toEqual(["parent"]);
    expect(bySession.get("parent")?.map((item) => item.id)).toEqual(["a"]);
  });
});

describe("buildAgentsLineage", () => {
  it("splits running agents, sessions it started, and earlier agents newest first", () => {
    const sessions = [
      session("parent"),
      session("fork-waiting", { forkedFromSessionId: "parent", spawnKind: "fork", sessionStatus: "waiting_input" }),
      session("started-idle", { spawnedBySessionId: "parent", spawnKind: "api" }),
      session("unrelated", { forkedFromSessionId: "elsewhere", spawnKind: "fork" }),
    ];
    const items = [
      work("running"),
      work("shell", { kind: "shell" }),
      ended("older", "2026-10-04T10:01:00Z"),
      ended("newer", "2026-10-04T10:03:00Z"),
      work("failed", { status: "error", endedAt: "2026-10-04T10:02:00Z", endedReason: "lost" }),
    ];

    const lineage = buildAgentsLineage("parent", sessions, items);

    expect(lineage.running.map((row) => [row.key, row.state])).toEqual([
      ["work:running", "running"],
      ["session:fork-waiting", "waiting"],
    ]);
    expect(lineage.started.map((row) => [row.key, row.kind, row.state])).toEqual([["session:started-idle", "started", "idle"]]);
    expect(lineage.earlier.map((row) => [row.key, row.state])).toEqual([
      ["work:newer", "done"],
      ["work:failed", "failed"],
      ["work:older", "done"],
    ]);
  });

  it("lists a subagent's session once, through its work", () => {
    const sessions = [session("child-a", { parentSessionId: "parent" })];
    const lineage = buildAgentsLineage("parent", sessions, [work("a")]);

    expect(lineage.running.map((row) => row.key)).toEqual(["work:a"]);
    expect(lineage.running[0]).toMatchObject({ name: "code-reviewer", task: "Review the diff", sessionId: "child-a" });
  });
});

describe("summarizeAgentActivity", () => {
  it("reads what it was asked, its tool calls, tokens, latest words and model", () => {
    const messages = [
      { messageId: "u1", sessionId: "c", role: "user", parts: [{ partId: "t", type: "text", text: "Review the diff for the mapper\nthanks" }] },
      {
        messageId: "a1", sessionId: "c", role: "assistant", modelID: "claude-sonnet-5-5", tokens: { input: 30_000, output: 1_200, reasoning: 0 },
        parts: [
          { partId: "1", type: "tool", tool: "read", callId: "1", state: {} },
          { partId: "2", type: "tool", tool: "grep", callId: "2", state: {} },
          { partId: "3", type: "tool", tool: "read", callId: "3", state: {} },
          { partId: "4", type: "text", text: "Looking around.\nReading OpenCode2Delegations.cs to compare" },
        ],
      },
      {
        messageId: "a2", sessionId: "c", role: "assistant", tokens: { input: 8_200, output: 700, reasoning: 0 },
        parts: [{ partId: "5", type: "tool", tool: "mcp__fleet__bash", callId: "5", state: {} }],
      },
    ] as AccumulatedMessage[];

    expect(summarizeAgentActivity(messages)).toEqual({
      asked: "Review the diff for the mapper",
      toolCalls: 4,
      topTools: ["Read", "Grep", "Bash"],
      tokensIn: 38_200,
      tokensOut: 1_900,
      latest: "Reading OpenCode2Delegations.cs to compare",
      modelId: "claude-sonnet-5-5",
    });
  });
});
