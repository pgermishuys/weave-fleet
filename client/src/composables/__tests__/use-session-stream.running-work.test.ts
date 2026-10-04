import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import type { DomainEvent } from "@/lib/domain-events";
import type { SessionSnapshot } from "@/lib/session-snapshot";
import { flushAll, mountComposable } from "./test-utils";

const { subscribeV2Mock, apiGet } = vi.hoisted(() => ({
  subscribeV2Mock: vi.fn(),
  apiGet: vi.fn(),
}));

vi.mock("@/composables/use-weave-socket", () => ({
  useWeaveSocket: () => ({ subscribeV2: subscribeV2Mock }),
  loadSessionHistory: vi.fn(),
}));

vi.mock("@/composables/use-signalr-socket", () => ({
  onGlobalEvent: () => () => undefined,
  onReconnect: () => () => undefined,
}));

vi.mock("@/api/client", () => ({ api: { GET: apiGet, POST: vi.fn() } }));

type Listener = { onSnapshot: (snapshot: SessionSnapshot) => void; onEvent: (event: DomainEvent) => void };
const listeners = new Map<string, Listener>();

const shell = {
  id: "w-shell", sessionId: "s1", workId: "sh_1", kind: "shell", title: "shell", label: "bun run test:e2e",
  status: "running", background: true, toolCallId: "call_bg", canStop: true, canReadOutput: true,
  startedAt: "2026-10-04T10:00:00Z",
};

function snapshot(runningWork: unknown[]): SessionSnapshot {
  return {
    session: { id: "s1", title: "s1", status: "active" },
    messages: [],
    delegations: [],
    runningWork,
    activityStatus: "idle",
    lastEventId: 1,
    hasMore: false,
    cursor: null,
    isPartial: false,
  };
}

describe("useSessionStream running work", () => {
  beforeEach(async () => {
    setActivePinia(createPinia());
    listeners.clear();
    subscribeV2Mock.mockReset();
    subscribeV2Mock.mockImplementation((topic: string, onSnapshot: Listener["onSnapshot"], onEvent: Listener["onEvent"]) => {
      listeners.set(topic, { onSnapshot, onEvent });
      return () => listeners.delete(topic);
    });
    apiGet.mockReset();
    apiGet.mockResolvedValue({ data: [], response: { ok: true, status: 200 } });
    const { _resetRunningWorkForTesting } = await import("@/composables/use-running-work");
    _resetRunningWorkForTesting();
    const { _resetKeptStreamsForTesting } = await import("@/composables/use-session-stream");
    _resetKeptStreamsForTesting();
  });

  it("serves the snapshot's work and the session's work events to useRunningWork", async () => {
    const { useSessionStream } = await import("@/composables/use-session-stream");
    const { useRunningWork } = await import("@/composables/use-running-work");
    const { result: stream } = await mountComposable(() => useSessionStream("s1"));
    listeners.get("session:s1")!.onSnapshot(snapshot([shell]));
    await flushAll();

    expect(stream.runningWork.value.map((item) => item.id)).toEqual(["w-shell"]);
    const { result: work } = await mountComposable(() => useRunningWork("s1"));
    expect(work.running.value.map((item) => item.label)).toEqual(["bun run test:e2e"]);
    // The stream had already said, so nothing was loaded.
    expect(apiGet).not.toHaveBeenCalled();

    listeners.get("session:s1")!.onEvent({
      type: "work.ended",
      payload: { ...shell, status: "completed", endedAt: "2026-10-04T10:03:00Z", endedReason: "completed", detail: "exit 0" } as never,
      eventId: 2,
    });
    // Live events wait for the next frame.
    await new Promise((resolve) => setTimeout(resolve, 150));
    await flushAll();

    expect(work.running.value).toEqual([]);
    expect(work.finished.value.map((item) => item.detail)).toEqual(["exit 0"]);
  });
});
