import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { shallowRef } from "vue";
import type { DomainEvent } from "@/lib/domain-events";
import type { SessionSnapshot } from "@/lib/session-snapshot";
import { flushAll, mountComposable } from "./test-utils";

const { subscribeV2Mock, setSessionFocusMock, getPreferencesMock } = vi.hoisted(() => ({
  subscribeV2Mock: vi.fn(),
  setSessionFocusMock: vi.fn(),
  getPreferencesMock: vi.fn(),
}));

vi.mock("@/api/client", () => ({ api: { GET: getPreferencesMock, PUT: vi.fn() } }));

vi.mock("@/composables/use-weave-socket", () => ({
  useWeaveSocket: () => ({ subscribeV2: subscribeV2Mock }),
  setSessionFocus: setSessionFocusMock,
}));

type Listener = { onSnapshot: (snapshot: SessionSnapshot) => void; onEvent: (event: DomainEvent) => void };
const listeners = new Map<string, Listener>();
let hasFocus = true;
let visibility: DocumentVisibilityState = "visible";

function snapshot(recapText?: string): SessionSnapshot {
  return {
    session: { id: "s1", title: "Live progress", status: "active" },
    messages: [],
    delegations: [],
    activityStatus: "idle",
    lastEventId: null,
    hasMore: false,
    cursor: null,
    isPartial: false,
    recap: recapText ? { sessionId: "s1", text: recapText, writtenAt: "2026-09-13T14:39:00Z" } : null,
  };
}

function recapEvent(sessionId: string, text: string | null): DomainEvent {
  return { type: "session.recap", payload: { sessionId, text, writtenAt: text ? "2026-09-13T14:39:00Z" : null } };
}

async function mountRecap(sessionId = shallowRef<string | null>("s1"), recapOn = true) {
  getPreferencesMock.mockResolvedValue({ data: recapOn ? { SessionRecap: "true" } : {} });
  const { useSessionRecap } = await import("@/composables/use-session-recap");
  const mounted = await mountComposable(() => useSessionRecap(sessionId));
  return { ...mounted, sessionId };
}

describe("useSessionRecap", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    subscribeV2Mock.mockReset();
    setSessionFocusMock.mockReset();
    getPreferencesMock.mockReset();
    listeners.clear();
    hasFocus = true;
    visibility = "visible";
    vi.spyOn(document, "hasFocus").mockImplementation(() => hasFocus);
    Object.defineProperty(document, "visibilityState", { configurable: true, get: () => visibility });
    subscribeV2Mock.mockImplementation((topic: string, onSnapshot: Listener["onSnapshot"], onEvent: Listener["onEvent"]) => {
      listeners.set(topic, { onSnapshot, onEvent });
      return () => listeners.delete(topic);
    });
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it("shows the recap from the snapshot and from session.recap, and hides it once cleared", async () => {
    const { result, wrapper } = await mountRecap();

    listeners.get("session:s1")!.onSnapshot(snapshot("You're adding live progress. Next, decide Y."));
    expect(result.value?.text).toBe("You're adding live progress. Next, decide Y.");

    listeners.get("session:s1")!.onEvent(recapEvent("other", "Not this session."));
    expect(result.value?.text).toBe("You're adding live progress. Next, decide Y.");

    listeners.get("session:s1")!.onEvent(recapEvent("s1", "A newer recap."));
    expect(result.value?.text).toBe("A newer recap.");

    listeners.get("session:s1")!.onEvent(recapEvent("s1", null));
    expect(result.value).toBeNull();
    wrapper.unmount();
  });

  it("says it's looking once subscribed, when Session recap is on", async () => {
    const { wrapper } = await mountRecap();

    expect(setSessionFocusMock).not.toHaveBeenCalled();
    listeners.get("session:s1")!.onSnapshot(snapshot());

    expect(setSessionFocusMock).toHaveBeenCalledExactlyOnceWith("s1", true);
    wrapper.unmount();
  });

  it("reports nothing when Session recap is off", async () => {
    const { wrapper } = await mountRecap(shallowRef("s1"), false);

    listeners.get("session:s1")!.onSnapshot(snapshot());
    window.dispatchEvent(new Event("blur"));
    await flushAll();

    expect(setSessionFocusMock).not.toHaveBeenCalled();
    wrapper.unmount();
  });

  it("says it looked away when the window loses focus or the tab is hidden, and back again", async () => {
    const { wrapper } = await mountRecap();
    listeners.get("session:s1")!.onSnapshot(snapshot());
    vi.useFakeTimers();

    hasFocus = false;
    window.dispatchEvent(new Event("blur"));
    vi.advanceTimersByTime(250);
    expect(setSessionFocusMock).toHaveBeenLastCalledWith("s1", false);

    hasFocus = true;
    window.dispatchEvent(new Event("focus"));
    vi.advanceTimersByTime(250);
    expect(setSessionFocusMock).toHaveBeenLastCalledWith("s1", true);

    visibility = "hidden";
    document.dispatchEvent(new Event("visibilitychange"));
    vi.advanceTimersByTime(250);
    expect(setSessionFocusMock).toHaveBeenLastCalledWith("s1", false);
    expect(setSessionFocusMock).toHaveBeenCalledTimes(4);
    wrapper.unmount();
  });

  it("ignores a blur that settles back to focus, like clicking into a canvas iframe", async () => {
    const { wrapper } = await mountRecap();
    listeners.get("session:s1")!.onSnapshot(snapshot());
    vi.useFakeTimers();

    window.dispatchEvent(new Event("blur"));
    vi.advanceTimersByTime(250);

    expect(setSessionFocusMock).toHaveBeenCalledOnce();
    wrapper.unmount();
  });

  it("stops looking at the old session when you open another", async () => {
    const { wrapper, sessionId } = await mountRecap();
    listeners.get("session:s1")!.onSnapshot(snapshot("Old recap."));

    sessionId.value = "s2";
    await flushAll();
    listeners.get("session:s2")!.onSnapshot({ ...snapshot(), session: { id: "s2", title: "Other", status: "active" } });

    expect(setSessionFocusMock.mock.calls).toEqual([["s1", true], ["s1", false], ["s2", true]]);
    wrapper.unmount();
  });

  it("says it again after a reconnect restores the subscription", async () => {
    const { wrapper } = await mountRecap();
    listeners.get("session:s1")!.onSnapshot(snapshot());

    listeners.get("session:s1")!.onSnapshot(snapshot());

    expect(setSessionFocusMock.mock.calls).toEqual([["s1", true], ["s1", true]]);
    wrapper.unmount();
  });
});
