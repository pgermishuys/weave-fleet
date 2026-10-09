import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { shallowRef } from "vue";
import type { DomainEvent } from "@/lib/domain-events";
import { useAgentBrowserStore } from "@/stores/agent-browser";
import { flushAll, mountComposable } from "./test-utils";

const { subscribeV2Mock, apiFetchOnMock, reconnectCallbacks } = vi.hoisted(() => ({
  subscribeV2Mock: vi.fn(),
  apiFetchOnMock: vi.fn(),
  reconnectCallbacks: [] as (() => void)[],
}));

vi.mock("@/lib/api-client", () => ({ apiFetchOn: apiFetchOnMock }));
vi.mock("@/composables/use-weave-socket", () => ({
  useWeaveSocket: () => ({ subscribeV2: subscribeV2Mock }),
  onReconnect: (_machine: unknown, callback: () => void) => {
    reconnectCallbacks.push(callback);
    return () => reconnectCallbacks.splice(reconnectCallbacks.indexOf(callback), 1);
  },
}));

type OnEvent = (event: DomainEvent) => void;
const listeners = new Map<string, OnEvent>();

const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });

function step(sessionId: string, seq: number, extra: Record<string, unknown> = {}): DomainEvent {
  return {
    type: "browser.step",
    payload: { sessionId, seq, kind: "tabs.open", ok: true, tabId: "tab-1", url: "http://localhost:4000/", title: "Demo app", ...extra },
  } as unknown as DomainEvent;
}

async function mountBrowser(sessionId = shallowRef<string | null>("s1")) {
  const { useAgentBrowser } = await import("@/composables/use-agent-browser");
  const mounted = await mountComposable(() => useAgentBrowser(sessionId));
  return { ...mounted, sessionId, push: (topic: string, event: DomainEvent) => listeners.get(topic)?.(event) };
}

describe("useAgentBrowser", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    subscribeV2Mock.mockReset().mockImplementation((topic: string, _onSnapshot: unknown, onEvent: OnEvent) => {
      listeners.set(topic, onEvent);
      return () => listeners.delete(topic);
    });
    apiFetchOnMock.mockReset().mockImplementation(() => Promise.resolve(json({ tabs: [], focusedTabId: null, steps: [] })));
    reconnectCallbacks.length = 0;
    listeners.clear();
  });

  it("loads the agent's tabs and steps when the session opens", async () => {
    apiFetchOnMock.mockImplementation(() => Promise.resolve(json({
      tabs: [{ id: "tab-1", url: "http://localhost:4000/", title: "Demo app", loading: false, canGoBack: false, canGoForward: false, generation: 1 }],
      focusedTabId: "tab-1",
      steps: [],
    })));

    await mountBrowser();

    const store = useAgentBrowserStore();
    expect(apiFetchOnMock.mock.calls[0]?.[1]).toBe("/api/sessions/s1/agent-browser");
    expect(store.of("s1").tabs.map((tab) => tab.id)).toEqual(["tab-1"]);
    expect(store.of("s1").focusedTabId).toBe("tab-1");
  });

  it("applies browser.step events from the session's topic", async () => {
    const { push } = await mountBrowser();

    push("session:s1", step("s1", 1));
    push("session:s1", step("s1", 1));

    const store = useAgentBrowserStore();
    expect(store.of("s1").steps).toHaveLength(1);
    expect(store.of("s1").tabs[0]?.title).toBe("Demo app");
  });

  it("ignores other events and steps for another session", async () => {
    const { push } = await mountBrowser();

    push("session:s1", step("other", 1));
    push("session:s1", { type: "session.queue", payload: { sessionId: "s1", items: [] } } as DomainEvent);

    expect(useAgentBrowserStore().of("s1").steps).toHaveLength(0);
  });

  it("loads again after a reconnect", async () => {
    await mountBrowser();
    expect(apiFetchOnMock).toHaveBeenCalledTimes(1);

    reconnectCallbacks[0]?.();
    await flushAll();

    expect(apiFetchOnMock).toHaveBeenCalledTimes(2);
  });

  it("follows the session when it changes, and does nothing without one", async () => {
    const { sessionId } = await mountBrowser();

    sessionId.value = "s2";
    await flushAll();
    expect(listeners.has("session:s1")).toBe(false);
    expect(listeners.has("session:s2")).toBe(true);

    sessionId.value = null;
    await flushAll();
    expect(listeners.size).toBe(0);
  });
});
