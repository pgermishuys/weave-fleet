import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import type { DomainEvent } from "@/lib/domain-events";
import type { SessionListItem } from "@/api/client";
import { useSessionsStore } from "@/stores/sessions";
import { mountComposable } from "./test-utils";

const { onGlobalEventMock } = vi.hoisted(() => ({ onGlobalEventMock: vi.fn() }));
vi.mock("@/composables/use-signalr-socket", () => ({ onGlobalEvent: onGlobalEventMock }));

let handler: ((event: DomainEvent) => void) | null = null;

describe("useSessionTokenUpdates", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    handler = null;
    onGlobalEventMock.mockReset();
    onGlobalEventMock.mockImplementation((_machine: unknown, _topic: string, callback: (event: DomainEvent) => void) => {
      handler = callback;
      return () => {};
    });
  });

  it("puts a session's new totals on its row as soon as they are pushed", async () => {
    const store = useSessionsStore();
    store.upsertSession({ session: { id: "s1", title: "One" }, totalTokens: 0, totalCost: 0 } as unknown as SessionListItem);
    const { useSessionTokenUpdates } = await import("@/composables/use-session-token-updates");
    await mountComposable(() => useSessionTokenUpdates());

    handler?.({ type: "session_tokens", payload: { sessionId: "s1", totalTokens: 14529, totalCost: 0.035 } } as unknown as DomainEvent);

    const row = store.sessions.find((item) => item.session.id === "s1");
    expect(row?.totalTokens).toBe(14529);
    expect(row?.totalCost).toBe(0.035);
  });

  it("ignores other events and malformed payloads", async () => {
    const store = useSessionsStore();
    store.upsertSession({ session: { id: "s1", title: "One" }, totalTokens: 5, totalCost: 0 } as unknown as SessionListItem);
    const { useSessionTokenUpdates } = await import("@/composables/use-session-token-updates");
    await mountComposable(() => useSessionTokenUpdates());

    handler?.({ type: "session_progress", payload: { sessionId: "s1", totalTokens: 99, totalCost: 1 } } as unknown as DomainEvent);
    handler?.({ type: "session_tokens", payload: { sessionId: "s1", totalTokens: "lots" } } as unknown as DomainEvent);

    expect(store.sessions.find((item) => item.session.id === "s1")?.totalTokens).toBe(5);
  });
});
