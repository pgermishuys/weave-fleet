import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import type { SessionListItem } from "@/api/client";
import type { DomainEvent } from "@/lib/domain-events";
import { useSessionsStore } from "@/stores/sessions";
import { mountComposable } from "./test-utils";

const { handlers } = vi.hoisted(() => ({ handlers: new Set<(event: unknown) => void>() }));

vi.mock("@/composables/use-signalr-socket", () => ({
  onGlobalEvent: (_machine: unknown, _topic: string, handler: (event: unknown) => void) => {
    handlers.add(handler);
    return () => handlers.delete(handler);
  },
  onReconnect: () => () => {},
}));

import { useSessionCheckoutUpdates } from "@/composables/use-session-checkout-updates";

const row = (id: string, workspaceId: string) =>
  ({ workspaceId, session: { id, title: id, time: { created: 1, updated: 1 }, tags: [] }, tags: [] }) as unknown as SessionListItem;

function push(payload: unknown): void {
  for (const handler of handlers) handler({ type: "session_checkout", payload } as DomainEvent);
}

describe("useSessionCheckoutUpdates", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    handlers.clear();
  });

  it("patches every session in the workspace through removed, restoring and restored", async () => {
    const store = useSessionsStore();
    store.setSessions([row("a", "ws-1"), row("a-child", "ws-1"), row("b", "ws-2")]);
    await mountComposable(() => useSessionCheckoutUpdates());

    push({ workspaceId: "ws-1", state: "removed", removedAt: "2026-10-01T10:00:00Z" });
    expect(store.sessionById("a")?.checkoutRemovedAt).toBe("2026-10-01T10:00:00Z");
    expect(store.sessionById("a-child")?.checkoutRemovedAt).toBe("2026-10-01T10:00:00Z");
    expect(store.sessionById("b")?.checkoutRemovedAt).toBeUndefined();

    push({ workspaceId: "ws-1", state: "restoring", removedAt: null });
    expect(store.sessionById("a")?.checkoutRestoring).toBe(true);
    expect(store.sessionById("a")?.checkoutRemovedAt).toBe("2026-10-01T10:00:00Z");

    push({ workspaceId: "ws-1", state: "restored", removedAt: null });
    expect(store.sessionById("a")?.checkoutRemovedAt).toBeNull();
    expect(store.sessionById("a")?.checkoutRestoring).toBe(false);
  });
});
