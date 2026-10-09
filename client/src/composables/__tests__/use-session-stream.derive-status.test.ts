import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { shallowRef } from "vue";
import type { SessionListItem } from "@/api/client";
import type { SessionSnapshot } from "@/lib/session-snapshot";
import { useSessionsStore } from "@/stores/sessions";
import { mountComposable } from "./test-utils";

const { subscribeV2Mock } = vi.hoisted(() => ({ subscribeV2Mock: vi.fn() }));

vi.mock("@/composables/use-weave-socket", () => ({
  useWeaveSocket: () => ({ subscribeV2: subscribeV2Mock }),
  loadSessionHistory: vi.fn(),
}));
vi.mock("@/composables/use-signalr-socket", () => ({
  onGlobalEvent: () => () => undefined,
}));

let onSnapshot: ((snapshot: SessionSnapshot) => void) | null = null;

function snapshot(activityStatus: string): SessionSnapshot {
  return {
    session: { id: "s1", title: "One", status: "active" },
    messages: [],
    delegations: [],
    activityStatus,
    lastEventId: null,
    hasMore: false,
    cursor: null,
    isPartial: false,
  } as unknown as SessionSnapshot;
}

describe("useSessionStream status derivation", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    onSnapshot = null;
    subscribeV2Mock.mockReset();
    subscribeV2Mock.mockImplementation((_topic: string, snap: (snapshot: SessionSnapshot) => void) => {
      onSnapshot = snap;
      return () => undefined;
    });
  });

  // The same table as use-session-activity-updates.test.ts: the two copies of deriveSessionStatus must agree.
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
  ])("derives the status for a snapshot with activity %s on a %s session as %s", async (activityStatus, current, expected) => {
    const store = useSessionsStore();
    store.upsertSession({ session: { id: "s1", title: "One" }, sessionStatus: current } as unknown as SessionListItem);
    const { useSessionStream } = await import("@/composables/use-session-stream");
    await mountComposable(() => useSessionStream(shallowRef("s1")));

    onSnapshot?.(snapshot(activityStatus));

    const item = store.sessions.find((entry) => entry.session.id === "s1");
    expect(item?.activityStatus).toBe(activityStatus);
    expect(item?.sessionStatus).toBe(expected);
  });
});
