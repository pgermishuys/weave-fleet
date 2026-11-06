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

import { useSessionBranchUpdates } from "@/composables/use-session-branch-updates";

const row = (id: string, workspaceId: string) =>
  ({ workspaceId, branch: "feature/one", session: { id, title: id, time: { created: 1, updated: 1 }, tags: [] }, tags: [] }) as unknown as SessionListItem;

describe("useSessionBranchUpdates", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    handlers.clear();
  });

  it("moves every session in the workspace to the new branch and leaves the others", async () => {
    const store = useSessionsStore();
    store.setSessions([row("a", "ws-1"), row("a-child", "ws-1"), row("b", "ws-2")]);
    await mountComposable(() => useSessionBranchUpdates());

    for (const handler of handlers) {
      handler({ type: "session_branch", payload: { workspaceId: "ws-1", branch: "feature/two" } } as DomainEvent);
    }

    expect(store.sessionById("a")?.branch).toBe("feature/two");
    expect(store.sessionById("a-child")?.branch).toBe("feature/two");
    expect(store.sessionById("b")?.branch).toBe("feature/one");
  });
});
