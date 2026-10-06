import { describe, expect, it } from "vitest";
import type { SessionListItem } from "@/api/client";
import { askPreview, buildInbox, machinesStatus, rowGlyph, rowMeta, rowPlace, type InboxMachineState } from "../inbox";

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

  it("says where a row's session is, by machine or by folder", () => {
    const inbox = buildInbox([machine("falcon", [
      session("c", "active", 6, { branch: "refactor/references", workspaceDirectory: "/home/me/src/weave-fleet" } as Partial<SessionListItem>),
      session("d", "idle", 22, { workspaceDisplayName: "weave", branch: null } as Partial<SessionListItem>),
    ])], NOW);
    expect(rowPlace(inbox.working[0], "machine")).toBe("falcon · refactor/references");
    expect(rowPlace(inbox.working[0], "folder")).toBe("weave-fleet · refactor/references");
    expect(rowPlace(inbox.finished[0], "folder")).toBe("weave");
    expect(rowPlace({ machineName: "hangar", folder: null, branch: null }, "folder")).toBe("hangar");
  });

  it("gives each row its glyph and what it says on the right", () => {
    const words = { duration: "6m 2s", short: "22m" };
    expect(rowGlyph({ status: "waiting_input" })).toBe("waiting");
    expect(rowGlyph({ status: "active" })).toBe("working");
    expect(rowGlyph({ status: "error" })).toBe("error");
    expect(rowGlyph({ status: "idle" })).toBe("quiet");
    expect(rowMeta({ status: "waiting_input" }, words)).toEqual({ text: "Needs you", tone: "waiting" });
    expect(rowMeta({ status: "error" }, words)).toEqual({ text: "Failed", tone: "error" });
    expect(rowMeta({ status: "active" }, words)).toEqual({ text: "6m 2s", tone: "quiet" });
    expect(rowMeta({ status: "idle" }, words)).toEqual({ text: "22m", tone: "quiet" });
  });
});

describe("the inbox's machines line", () => {
  it("gives each machine a dot, and says how they are", () => {
    expect(machinesStatus([{ name: "hangar", status: "live" }, { name: "falcon", status: "polling" }])).toEqual({
      machines: [{ name: "hangar", tone: "online" }, { name: "falcon", tone: "online" }],
      note: "online",
    });
    expect(machinesStatus([{ name: "hangar", status: "live" }, { name: "falcon", status: "unreachable" }]).note).toBe("falcon unreachable");
    expect(machinesStatus([{ name: "hangar", status: "live" }, { name: "falcon", status: "connecting" }]).note).toBe("1 connecting");
    expect(machinesStatus([{ name: "hangar", status: "connecting" }])).toEqual({ machines: [{ name: "hangar", tone: "connecting" }], note: "connecting…" });
    expect(machinesStatus([{ name: "a", status: "unreachable" }, { name: "b", status: "unreachable" }]).note).toBe("a and b unreachable");
  });
});
