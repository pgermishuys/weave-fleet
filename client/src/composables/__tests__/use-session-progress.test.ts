import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { shallowRef } from "vue";
import type { SessionListItem } from "@/api/client";
import type { DomainEvent } from "@/lib/domain-events";
import { useSessionProgressStore } from "@/stores/session-progress";
import { useSessionsStore } from "@/stores/sessions";
import { flushAll, mountComposable } from "./test-utils";

const { apiFetchMock, handlers, reconnectCallbacks } = vi.hoisted(() => ({
  apiFetchMock: vi.fn(),
  handlers: new Map<string, Set<(event: unknown) => void>>(),
  reconnectCallbacks: new Set<() => void>(),
}));

vi.mock("@/lib/api-client", () => ({ apiFetch: apiFetchMock }));

vi.mock("@/composables/use-signalr-socket", () => ({
  onGlobalEvent: (topic: string, handler: (event: unknown) => void) => {
    const set = handlers.get(topic) ?? new Set();
    set.add(handler);
    handlers.set(topic, set);
    return () => set.delete(handler);
  },
  onReconnect: (callback: () => void) => {
    reconnectCallbacks.add(callback);
    return () => reconnectCallbacks.delete(callback);
  },
}));

import { useSessionProgress } from "@/composables/use-session-progress";
import { useSessionProgressUpdates } from "@/composables/use-session-progress-updates";

function push(topic: string, type: string, payload: unknown): void {
  for (const handler of handlers.get(topic) ?? []) handler({ type, payload } as DomainEvent);
}

function detail(sessionId: string, done: number, updatedAt: string) {
  return {
    sessionId,
    kind: "todos",
    done,
    total: 2,
    current: done < 2 ? "Drop the indexes" : null,
    todos: [
      { content: "Write the migration", status: "completed", priority: "high" },
      { content: "Drop the indexes", status: done < 2 ? "in_progress" : "completed", priority: null },
    ],
    updatedAt,
  };
}

const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });

function row(id: string): SessionListItem {
  return { session: { id, title: id, time: { created: 1, updated: 1 }, tags: [] }, sessionStatus: "active", tags: [] } as unknown as SessionListItem;
}

describe("useSessionProgress", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    apiFetchMock.mockReset();
    handlers.clear();
    reconnectCallbacks.clear();
  });

  it("loads the session's todos and follows pushes on its topic", async () => {
    apiFetchMock.mockResolvedValue(json(detail("s1", 1, "2026-09-13T12:00:00Z")));

    const { result } = await mountComposable(() => useSessionProgress("s1"));

    expect(result.todos.value.map((todo) => todo.content)).toEqual(["Write the migration", "Drop the indexes"]);
    expect(result.progress.value?.done).toBe(1);

    push("session:s1", "progress.updated", detail("s1", 2, "2026-09-13T12:05:00Z"));
    expect(result.progress.value?.done).toBe(2);

    push("session:s1", "message.updated", detail("s1", 0, "2026-09-13T12:06:00Z"));
    push("session:s1", "progress.updated", detail("s2", 0, "2026-09-13T12:06:00Z"));
    expect(result.progress.value?.done).toBe(2);
  });

  it("moves to the new session's topic when the session changes", async () => {
    apiFetchMock.mockImplementation((path: string) =>
      Promise.resolve(json(detail(path.includes("s2") ? "s2" : "s1", 1, "2026-09-13T12:00:00Z"))));
    const sessionId = shallowRef("s1");

    const { result } = await mountComposable(() => useSessionProgress(sessionId));
    sessionId.value = "s2";
    await flushAll();

    expect(result.progress.value?.sessionId).toBe("s2");
    expect(handlers.get("session:s1")?.size ?? 0).toBe(0);
    expect(handlers.get("session:s2")?.size).toBe(1);
  });

  it("loads again after a reconnect", async () => {
    apiFetchMock
      .mockResolvedValueOnce(json(detail("s1", 1, "2026-09-13T12:00:00Z")))
      .mockResolvedValueOnce(json(detail("s1", 2, "2026-09-13T12:05:00Z")));

    const { result } = await mountComposable(() => useSessionProgress("s1"));
    for (const callback of reconnectCallbacks) callback();
    await flushAll();

    expect(apiFetchMock).toHaveBeenCalledTimes(2);
    expect(result.progress.value?.done).toBe(2);
  });

  it("stops listening when unmounted", async () => {
    apiFetchMock.mockResolvedValue(new Response(null, { status: 204 }));

    const { wrapper } = await mountComposable(() => useSessionProgress("s1"));
    wrapper.unmount();

    expect(handlers.get("session:s1")?.size ?? 0).toBe(0);
    expect(reconnectCallbacks.size).toBe(0);
  });
});

describe("useSessionProgressUpdates", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    handlers.clear();
  });

  it("updates the row of a session that isn't open", async () => {
    const sessions = useSessionsStore();
    sessions.setSessions([row("s1"), row("s2")]);
    await mountComposable(() => useSessionProgressUpdates());

    push("sessions", "session_progress", { sessionId: "s2", kind: "todos", done: 3, total: 7, current: "Next" });

    expect(sessions.sessions.find((item) => item.session.id === "s2")?.progress)
      .toEqual({ sessionId: "s2", kind: "todos", done: 3, total: 7, current: "Next" });
    expect(sessions.sessions.find((item) => item.session.id === "s1")?.progress).toBeUndefined();
  });

  it("ignores other events and malformed payloads", async () => {
    const sessions = useSessionsStore();
    sessions.setSessions([row("s1")]);
    await mountComposable(() => useSessionProgressUpdates());

    push("sessions", "activity_status", { sessionId: "s1", kind: "todos", done: 1, total: 2 });
    push("sessions", "session_progress", { sessionId: "s1", done: "many" });

    expect(sessions.sessions[0]?.progress).toBeUndefined();
  });

  it("drops a loaded detail that the summary shows is out of date", async () => {
    const progress = useSessionProgressStore();
    progress.apply({ ...detail("s1", 1, "2026-09-13T12:00:00Z"), todos: [], kind: "todos" } as never);
    await mountComposable(() => useSessionProgressUpdates());

    push("sessions", "session_progress", { sessionId: "s1", kind: "todos", done: 2, total: 2, current: null });

    expect("s1" in progress.bySession).toBe(false);
  });
});
