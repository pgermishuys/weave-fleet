import { describe, expect, it } from "vitest";
import { applyDomainEvent, createSessionStreamState } from "@/lib/domain-event-reducer";
import type { DomainEvent } from "@/lib/domain-events";
import {
  applyWorkItem,
  elapsedTickMs,
  formatAgo,
  formatElapsed,
  isWorkRunning,
  toRunningWorkItem,
  toRunningWorkItems,
  workResult,
  type RunningWorkItem,
} from "@/lib/running-work";
import type { SessionSnapshot } from "@/lib/session-snapshot";

/**
 * The payloads as the server sends them (SignalREventContractTests: RunningWorkItem, camelCase, nulls and false left
 * out): an OpenCode 2 background shell, and a subagent with a child session.
 */
const shellStarted = {
  id: "5f0c2b7e9d",
  sessionId: "s1",
  workId: "sh_1076",
  kind: "shell",
  title: "shell",
  label: "bun run test:e2e",
  status: "running",
  background: true,
  toolCallId: "call_bg",
  canStop: true,
  canReadOutput: true,
  startedAt: "2026-10-04T10:00:00Z",
};

const shellEnded = {
  ...shellStarted,
  status: "completed",
  endedAt: "2026-10-04T10:03:12Z",
  endedReason: "completed",
  detail: "exit 0",
};

const subagentStarted = {
  id: "a91d",
  sessionId: "s1",
  workId: "call_sub",
  kind: "subagent",
  title: "code-reviewer",
  label: "Review the diff",
  status: "running",
  background: true,
  childSessionId: "child-1",
  toolCallId: "call_sub",
  canStop: true,
  startedAt: "2026-10-04T10:01:00Z",
};

function item(payload: Record<string, unknown>): RunningWorkItem {
  const parsed = toRunningWorkItem(payload);
  if (!parsed) throw new Error("not an item");
  return parsed;
}

function snapshot(runningWork?: unknown[]): SessionSnapshot {
  return {
    session: { id: "s1", title: "s1", status: "active" },
    messages: [],
    delegations: [],
    ...(runningWork ? { runningWork } : {}),
    activityStatus: "idle",
    lastEventId: 1,
    hasMore: false,
    cursor: null,
    isPartial: false,
  };
}

function workEvent(type: "work.started" | "work.updated" | "work.ended", payload: Record<string, unknown>): DomainEvent {
  return { type, payload: payload as never, eventId: 2 };
}

describe("running work items", () => {
  it("reads the server's item, filling in what it leaves out", () => {
    const shell = item(shellStarted);
    expect(shell).toMatchObject({
      id: "5f0c2b7e9d",
      kind: "shell",
      label: "bun run test:e2e",
      background: true,
      canStop: true,
      canReadOutput: true,
      childSessionId: null,
      endedAt: null,
      endedReason: null,
      detail: null,
    });
    expect(isWorkRunning(shell)).toBe(true);

    // A subagent from OpenCode 2 has no output of its own: canReadOutput is left out, so it's false.
    expect(item(subagentStarted)).toMatchObject({ canReadOutput: false, childSessionId: "child-1" });
  });

  it("drops what isn't an item and reads an unknown kind as a task", () => {
    expect(toRunningWorkItem(null)).toBeNull();
    expect(toRunningWorkItem({ sessionId: "s1" })).toBeNull();
    expect(toRunningWorkItem({ ...shellStarted, kind: "daemon" })?.kind).toBe("task");
    expect(toRunningWorkItems([shellStarted, 3, { id: "x" }]).map((work) => work.id)).toEqual(["5f0c2b7e9d"]);
  });

  it("replaces an item by id, keeps ended work ended, and returns the same list when nothing changed", () => {
    const started = [item(shellStarted)];
    const ended = applyWorkItem(started, item(shellEnded));
    expect(ended[0]).toMatchObject({ status: "completed", detail: "exit 0" });

    // A running event sent before the end but delivered after it doesn't bring the work back.
    expect(applyWorkItem(ended, item(shellStarted))).toBe(ended);
    expect(applyWorkItem(ended, item(shellEnded))).toBe(ended);
  });

  it("keeps items oldest first", () => {
    const list = applyWorkItem([item(subagentStarted)], item(shellStarted));
    expect(list.map((work) => work.kind)).toEqual(["shell", "subagent"]);
  });

  it("says how long and how it ended in a few words", () => {
    expect(formatElapsed(58_000)).toBe("58s");
    expect(formatElapsed(192_000)).toBe("3m");
    expect(formatElapsed(3_840_000)).toBe("1h 4m");
    expect(formatElapsed(7_200_000)).toBe("2h");
    expect(formatAgo(0)).toBe("just now");
    expect(formatAgo(59_000)).toBe("just now");
    expect(formatAgo(240_000)).toBe("4m ago");
    // Seconds tick in the first minute only; after that the minutes are all that changes.
    expect(elapsedTickMs(12_000)).toBe(1_000);
    expect(elapsedTickMs(61_000)).toBe(60_000);
    expect(workResult(item(shellStarted))).toBeNull();
    expect(workResult(item(shellEnded))).toBe("exit 0");
    expect(workResult(item({ ...shellStarted, status: "cancelled", endedAt: "2026-10-04T10:01:00Z", endedReason: "cancelled" }))).toBe("stopped");
    expect(workResult(item({ ...shellStarted, status: "error", endedAt: "2026-10-04T10:01:00Z", endedReason: "lost" }))).toBe("lost");
  });
});

describe("applyDomainEvent with work events", () => {
  it("starts from the snapshot's runningWork", () => {
    const state = createSessionStreamState(snapshot([subagentStarted, shellEnded]));
    expect(state.runningWork.map((work) => [work.kind, work.status])).toEqual([["shell", "completed"], ["subagent", "running"]]);
  });

  it("starts with no work when the snapshot has none", () => {
    expect(createSessionStreamState(snapshot()).runningWork).toEqual([]);
  });

  it("adds work on work.started, changes it on work.updated and ends it on work.ended", () => {
    let state = createSessionStreamState(snapshot());
    state = applyDomainEvent(state, workEvent("work.started", shellStarted));
    expect(state.runningWork).toHaveLength(1);

    state = applyDomainEvent(state, workEvent("work.updated", { ...shellStarted, label: "bun run test:e2e --shard 1" }));
    expect(state.runningWork[0].label).toBe("bun run test:e2e --shard 1");

    state = applyDomainEvent(state, workEvent("work.ended", shellEnded));
    expect(state.runningWork[0]).toMatchObject({ status: "completed", endedReason: "completed", detail: "exit 0" });
  });

  it("doesn't count background work as the session's own", () => {
    const state = applyDomainEvent(createSessionStreamState(snapshot()), workEvent("work.started", subagentStarted));
    expect(state.sessionStatus).toBe("idle");
  });

  it("keeps the same state for an event it already has", () => {
    const state = applyDomainEvent(createSessionStreamState(snapshot()), workEvent("work.started", shellStarted));
    expect(applyDomainEvent(state, workEvent("work.started", shellStarted))).toBe(state);
    expect(applyDomainEvent(state, { type: "work.ended", payload: { nope: true } as never })).toBe(state);
  });
});
