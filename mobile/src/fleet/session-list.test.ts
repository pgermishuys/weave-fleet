import { describe, expect, test } from "bun:test";
import type { SessionListItem } from "@fleet/api/client";
import type { DomainEvent } from "@fleet/lib/domain-events";
import { applySessionsEvent, rowDetail, sectionSessions, toDomainEvent } from "./session-list";

const item = (id: string, over: Partial<SessionListItem> & { updated?: number } = {}): SessionListItem => ({
  instanceId: "i", workspaceId: "w", workspaceDirectory: "/home/me/src/pay-app", workspaceDisplayName: "pay-app", isolationStrategy: "existing",
  sessionStatus: "idle", instanceStatus: "running", lifecycleStatus: "running", retentionStatus: "kept", typedInstanceStatus: "running",
  isHidden: false, harnessType: "opencode", tags: [], branch: "main",
  session: { id, title: id, time: { created: 1, updated: over.updated ?? 1 } } as SessionListItem["session"],
  ...over,
} as SessionListItem);

const event = (type: string, payload: object) => ({ type, payload }) as unknown as DomainEvent;

describe("toDomainEvent", () => {
  test("the wire's properties become the payload", () => {
    expect(toDomainEvent(7, { type: "activity_status", properties: { sessionId: "s" } })).toEqual({ type: "activity_status", payload: { sessionId: "s" }, eventId: 7 });
    expect(toDomainEvent(null, { type: "x", properties: 1 })).toEqual({ type: "x", payload: 1 });
  });
});

describe("applySessionsEvent", () => {
  test("activity status moves a session between sections", () => {
    const { list, refetch } = applySessionsEvent([item("a")], event("activity_status", { sessionId: "a", activityStatus: "busy" }));
    expect(list[0].sessionStatus).toBe("active");
    expect(refetch).toBe(false);
  });
  test("waiting_input needs you; an unknown session asks for a refetch", () => {
    expect(applySessionsEvent([item("a")], event("activity_status", { sessionId: "a", activityStatus: "waiting_input" })).list[0].sessionStatus).toBe("waiting_input");
    expect(applySessionsEvent([], event("activity_status", { sessionId: "z", activityStatus: "busy" })).refetch).toBe(true);
  });
  test("archived and deleted leave the list; created refetches", () => {
    expect(applySessionsEvent([item("a"), item("b")], event("session_archived", { sessionId: "a" })).list.map((i) => i.session.id)).toEqual(["b"]);
    expect(applySessionsEvent([item("a")], event("session_deleted", { sessionId: "a" })).list).toEqual([]);
    expect(applySessionsEvent([], event("session_created", { sessionId: "n" })).refetch).toBe(true);
  });
  test("other events change nothing", () => {
    const list = [item("a")];
    expect(applySessionsEvent(list, event("work.started", {}))).toEqual({ list, refetch: false });
  });
});

describe("sectionSessions", () => {
  test("sections, newest first, top-level only, across machines", () => {
    const sections = sectionSessions([
      { machineId: "m1", machineName: "Hangar", items: [item("old", { updated: 5 }), item("busy", { sessionStatus: "active", updated: 9 }), item("child", { parentSessionId: "busy" }), item("gone", { archivedAt: "2026-01-01" })] },
      { machineId: "m2", machineName: "Laptop", items: [item("ask", { sessionStatus: "waiting_input" }), item("new", { updated: 8 }), item("hidden", { isHidden: true })] },
    ]);
    expect(sections.map((s) => [s.label, s.data.map((e) => e.item.session.id)])).toEqual([
      ["Needs you", ["ask"]],
      ["Working", ["busy"]],
      ["Recent", ["new", "old"]],
    ]);
    expect(sections[0].data[0].machineName).toBe("Laptop");
  });
  test("an error needs you; empty sections vanish", () => {
    const sections = sectionSessions([{ machineId: "m", machineName: "M", items: [item("bad", { sessionStatus: "error" })] }]);
    expect(sections.map((s) => s.label)).toEqual(["Needs you"]);
  });
});

test("rowDetail names the folder and branch", () => {
  expect(rowDetail(item("a"))).toBe("pay-app · main");
  expect(rowDetail(item("a", { workspaceDisplayName: null, branch: null }))).toBe("pay-app");
});
