import { describe, expect, it } from "vitest";
import type { SessionListItem } from "@/api/client";
import { askPreview, buildInbox, machinesLine, rowInfo, sessionLine, type InboxMachineState } from "../inbox";

const NOW = Date.parse("2026-10-05T14:07:00Z");

function session(id: string, status: string, minutesAgo: number, extra: Partial<SessionListItem> = {}): SessionListItem {
  const updated = NOW - minutesAgo * 60_000;
  return {
    session: { id, title: `Session ${id}`, time: { created: updated, updated } },
    instanceId: `inst-${id}`,
    sessionStatus: status,
    activityStatus: status === "active" ? "busy" : "idle",
    retentionStatus: "active",
    parentSessionId: null,
    ...extra,
  } as SessionListItem;
}

function machine(id: string, sessions: SessionListItem[], extra: Partial<InboxMachineState> = {}): InboxMachineState {
  return { id, name: id, isHome: id === "hangar", status: "live", lastHeardAt: NOW, sessions, asks: {}, ...extra };
}

describe("buildInbox", () => {
  it("puts what needs you first, across machines, newest first", () => {
    const inbox = buildInbox([
      machine("hangar", [session("a", "waiting_input", 3), session("b", "idle", 22), session("c", "active", 6)]),
      machine("falcon", [session("d", "waiting_input", 11), session("e", "error", 1)]),
    ], NOW);

    expect(inbox.needsYou.map((i) => `${i.machineId}/${i.sessionId}`)).toEqual(["falcon/e", "hangar/a", "falcon/d"]);
    expect(inbox.working.map((i) => i.sessionId)).toEqual(["c"]);
    expect(inbox.finished.map((i) => i.sessionId)).toEqual(["b"]);
  });

  it("leaves out subagents, archived sessions and what finished over a day ago", () => {
    const inbox = buildInbox([
      machine("hangar", [
        session("child", "waiting_input", 1, { parentSessionId: "a" } as Partial<SessionListItem>),
        session("archived", "waiting_input", 1, { retentionStatus: "archived" } as Partial<SessionListItem>),
        session("old", "idle", 25 * 60),
      ]),
    ], NOW);

    expect(inbox.needsYou).toEqual([]);
    expect(inbox.finished).toEqual([]);
  });

  it("marks an unreachable machine's sessions as stale and carries asks", () => {
    const ask = { kind: "permission" as const, ask: { id: "p1", sessionId: "a", kind: "shell" as const, tool: "bash", title: "dotnet test", always: [], askedAt: "" } };
    const inbox = buildInbox([machine("falcon", [session("a", "waiting_input", 3)], { status: "unreachable", asks: { a: ask } })], NOW);

    expect(inbox.needsYou[0].stale).toBe(true);
    expect(inbox.needsYou[0].ask).toEqual(ask);
  });
});

describe("previews", () => {
  it("says what the agent wants", () => {
    const [entry] = buildInbox([machine("hangar", [session("a", "waiting_input", 3)], {
      asks: { a: { kind: "permission", ask: { id: "p1", sessionId: "a", kind: "shell", tool: "bash", title: "dotnet test", always: [], askedAt: "" } } },
    })], NOW).needsYou;
    expect(askPreview(entry)).toEqual({ lead: "Wants to run", detail: "dotnet test", code: true });
  });

  it("shows the question", () => {
    const [entry] = buildInbox([machine("falcon", [session("q", "waiting_input", 3)], {
      asks: { q: { kind: "question", requestId: "call-1", more: 0, question: { header: "401", question: "Plain 401 or a page?", options: [] } } },
    })], NOW).needsYou;
    expect(askPreview(entry)).toEqual({ lead: "Asked:", detail: "Plain 401 or a page?", code: false });
  });

  it("explains errors and unknown waits", () => {
    const inbox = buildInbox([machine("hangar", [session("e", "error", 1), session("w", "waiting_input", 2)])], NOW);
    expect(askPreview(inbox.needsYou[0]).lead).toBe("Stopped with an error");
    expect(askPreview(inbox.needsYou[1]).lead).toBe("Waiting on you");
  });

  it("writes the row line", () => {
    const inbox = buildInbox([machine("falcon", [session("c", "active", 6), session("b", "idle", 22)])], NOW);
    expect(rowInfo(inbox.working[0], "6m")).toBe("falcon · Working · 6m");
    expect(rowInfo(inbox.finished[0], "22m")).toBe("falcon · 22m ago");
    const stale = buildInbox([machine("falcon", [session("c", "active", 6)], { status: "unreachable" })], NOW);
    expect(rowInfo(stale.working[0], "6m")).toBe("falcon · Working when last heard · 6m");
  });
});

describe("the inbox's machines line", () => {
  it("names the machines that answer, and the ones that don't", () => {
    expect(machinesLine([{ name: "hangar", status: "live" }, { name: "falcon", status: "polling" }])).toEqual({ text: "hangar and falcon online", tone: "online" });
    expect(machinesLine([{ name: "hangar", status: "live" }, { name: "falcon", status: "unreachable" }])).toEqual({ text: "hangar online · falcon unreachable", tone: "partial" });
    expect(machinesLine([{ name: "hangar", status: "unreachable" }])).toEqual({ text: "hangar unreachable", tone: "offline" });
    expect(machinesLine([{ name: "hangar", status: "connecting" }])).toEqual({ text: "Connecting…", tone: "connecting" });
    expect(machinesLine([{ name: "a", status: "live" }, { name: "b", status: "live" }, { name: "c", status: "live" }]).text).toBe("a, b and c online");
  });

  it("says what a session row is doing", () => {
    const base = { key: "k", machineId: "m", machineName: "hangar", sessionId: "s", title: "T", updatedAt: 0, activity: null, ask: null, stale: false };
    expect(sessionLine({ ...base, status: "active" }, { duration: "6m", ago: "3 min ago" })).toBe("hangar · Working · 6m");
    expect(sessionLine({ ...base, status: "waiting_input" }, { duration: "6m", ago: "3 min ago" })).toBe("hangar · Needs you · 3 min ago");
    expect(sessionLine({ ...base, status: "idle" }, { duration: "6m", ago: "22 min ago" })).toBe("hangar · 22 min ago");
  });
});
