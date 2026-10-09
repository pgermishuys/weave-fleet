import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import type { DomainEvent } from "@/lib/domain-events";
import type { SessionListItem } from "@/api/client";
import { useSessionsStore } from "@/stores/sessions";
import { mountComposable } from "./test-utils";

const { onGlobalEventMock } = vi.hoisted(() => ({ onGlobalEventMock: vi.fn() }));
vi.mock("@/composables/use-signalr-socket", () => ({ onGlobalEvent: onGlobalEventMock }));

let handler: ((event: DomainEvent) => void) | null = null;
let unsubscribe: ReturnType<typeof vi.fn>;

function row(id: string, sessionStatus: string): SessionListItem {
  return { session: { id, title: `Session ${id}` }, sessionStatus, activityStatus: "idle" } as unknown as SessionListItem;
}

function send(type: string, payload: unknown): void {
  handler?.({ type, payload } as unknown as DomainEvent);
}

function find(store: ReturnType<typeof useSessionsStore>, id: string) {
  return store.sessions.find((entry) => entry.session.id === id);
}

async function mountUpdates() {
  const { useSessionActivityUpdates } = await import("@/composables/use-session-activity-updates");
  return mountComposable(() => useSessionActivityUpdates());
}

describe("useSessionActivityUpdates", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    handler = null;
    unsubscribe = vi.fn();
    onGlobalEventMock.mockReset();
    onGlobalEventMock.mockImplementation((_machine: unknown, topic: string, callback: (event: DomainEvent) => void) => {
      expect(topic).toBe("sessions");
      handler = callback;
      return unsubscribe;
    });
  });

  it("puts a session's activity and derived status on its row", async () => {
    const store = useSessionsStore();
    store.upsertSession(row("s1", "idle"));
    await mountUpdates();

    send("activity_status", { sessionId: "s1", activityStatus: "busy" });

    expect(find(store, "s1")?.activityStatus).toBe("busy");
    expect(find(store, "s1")?.sessionStatus).toBe("active");
  });

  it("keeps retry details only while the session is retrying", async () => {
    const store = useSessionsStore();
    store.upsertSession(row("s1", "active"));
    await mountUpdates();

    send("activity_status", { sessionId: "s1", activityStatus: "retry", attempt: 2, maxAttempts: 5, message: "Slow down", next: "soon" });
    expect(find(store, "s1")).toMatchObject({ retryAttempt: 2, retryMaxAttempts: 5, retryMessage: "Slow down", retryNext: "soon", sessionStatus: "active" });

    send("activity_status", { sessionId: "s1", activityStatus: "busy" });
    expect(find(store, "s1")).toMatchObject({ retryAttempt: null, retryMaxAttempts: null, retryMessage: null, retryNext: null });
  });

  it("clears an optimistic busy override when activity arrives", async () => {
    const store = useSessionsStore();
    store.upsertSession(row("s1", "idle"));
    await mountUpdates();
    const clear = vi.spyOn(store, "clearSessionStateOverride");

    send("activity_status", { sessionId: "s1", activityStatus: "idle" });

    expect(clear).toHaveBeenCalledWith("s1");
  });

  it("puts a scheduled retry on the row for session.retry, and takes it off again", async () => {
    const store = useSessionsStore();
    store.upsertSession(row("s1", "idle"));
    await mountUpdates();

    send("session.retry", {
      sessionId: "s1",
      retry: { dueAt: "2026-10-09T10:00:00Z", attempt: 1, kind: "rate_limit", reason: "Limit reached", providerSaid: true },
    });
    expect(find(store, "s1")?.scheduledRetry).toBeTruthy();

    send("session.retry", { sessionId: "s1", retry: null });
    expect(find(store, "s1")?.scheduledRetry ?? null).toBeNull();
  });

  it("ignores other events and activity without a session or status", async () => {
    const store = useSessionsStore();
    store.upsertSession(row("s1", "idle"));
    await mountUpdates();

    send("session_tokens", { sessionId: "s1", activityStatus: "busy" });
    send("activity_status", { activityStatus: "busy" });
    send("activity_status", { sessionId: "s1" });

    expect(find(store, "s1")?.activityStatus).toBe("idle");
  });

  it("stops listening when unmounted", async () => {
    const { wrapper } = await mountUpdates();
    wrapper.unmount();
    expect(unsubscribe).toHaveBeenCalledOnce();
  });

  // deriveSessionStatus, through the event path. The same table is pinned for the snapshot path in
  // use-session-stream.derive-status.test.ts.
  it.each([
    ["idle", "active", "idle"],
    ["waiting_input", "idle", "waiting_input"],
    ["busy", "idle", "active"],
    ["delegating", "idle", "active"],
    ["retry", "idle", "active"],
    ["something-new", "idle", "active"],
    ["idle", undefined, "idle"],
    ["busy", "stopped", "stopped"],
    ["busy", "completed", "completed"],
    ["idle", "error", "error"],
    ["waiting_input", "disconnected", "disconnected"],
  ])("derives the status for activity %s on a %s session as %s", async (activityStatus, current, expected) => {
    const store = useSessionsStore();
    store.upsertSession({ session: { id: "s1", title: "One" }, sessionStatus: current } as unknown as SessionListItem);
    await mountUpdates();

    send("activity_status", { sessionId: "s1", activityStatus });

    expect(find(store, "s1")?.sessionStatus).toBe(expected);
  });
});
