import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import type { DomainEvent } from "@/lib/domain-events";
import { MACHINE_TARGET, type MachineTarget } from "@/lib/machine-target";
import type { SessionListItem } from "@/api/client";
import { useSessionsStore } from "@/stores/sessions";
import { mountComposable } from "./test-utils";

const { subscribeV2Mock, globalHandlers } = vi.hoisted(() => ({
  subscribeV2Mock: vi.fn(),
  globalHandlers: [] as Array<(event: DomainEvent) => void>,
}));

vi.mock("@/composables/use-weave-socket", () => ({
  useWeaveSocket: () => ({ subscribeV2: subscribeV2Mock }),
  loadSessionHistory: vi.fn(),
}));
vi.mock("@/composables/use-signalr-socket", () => ({
  onGlobalEvent: (_machine: unknown, _topic: string, handler: (event: DomainEvent) => void) => {
    globalHandlers.push(handler);
    return () => undefined;
  },
}));

const remote = { key: "node-1", connection: { id: "node-1" }, isLive: false, api: {} } as unknown as MachineTarget;

describe("a session on another machine", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    globalHandlers.length = 0;
    subscribeV2Mock.mockReset();
    subscribeV2Mock.mockImplementation(() => () => undefined);
  });

  it("goes idle on its row when that machine says so, after the row was set busy by a send", async () => {
    const store = useSessionsStore();
    store.upsertElsewhere("node-1", { session: { id: "s1" }, activityStatus: "busy", sessionStatus: "active" } as unknown as SessionListItem);
    const { useSessionStream } = await import("@/composables/use-session-stream");
    await mountComposable(() => useSessionStream("s1"), { provide: { [MACHINE_TARGET]: () => remote } });

    for (const handler of globalHandlers) {
      handler({ type: "activity_status", payload: { sessionId: "s1", activityStatus: "idle" } } as unknown as DomainEvent);
    }

    expect(store.sessionById("s1")?.activityStatus).toBe("idle");
  });
});
