import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { shallowRef } from "vue";
import type { DomainEvent } from "@/lib/domain-events";
import { flushAll, mountComposable } from "./test-utils";

const { subscribeV2Mock, getMock, postMock, reconnectCallbacks } = vi.hoisted(() => ({
  subscribeV2Mock: vi.fn(),
  getMock: vi.fn(),
  postMock: vi.fn(),
  reconnectCallbacks: [] as (() => void)[],
}));

vi.mock("@/api/client", () => ({ api: { GET: getMock, POST: postMock } }));

vi.mock("@/composables/use-weave-socket", () => ({
  useWeaveSocket: () => ({ subscribeV2: subscribeV2Mock }),
  onReconnect: (callback: () => void) => {
    reconnectCallbacks.push(callback);
    return () => reconnectCallbacks.splice(reconnectCallbacks.indexOf(callback), 1);
  },
}));

type OnEvent = (event: DomainEvent) => void;
const listeners = new Map<string, OnEvent>();

const ask = {
  id: "per_1",
  sessionId: "s1",
  kind: "shell",
  tool: "bash",
  title: "git push origin main",
  always: ["git push *"],
  askedAt: "2026-09-28T10:00:00Z",
};

function asked(payload: Record<string, unknown>): DomainEvent {
  return { type: "permission.asked", payload };
}

function replied(id: string): DomainEvent {
  return { type: "permission.replied", payload: { id, sessionId: "s1", reply: "once" } };
}

async function mountPermissions(sessionId = shallowRef("s1")) {
  const { useSessionPermissions } = await import("@/composables/use-session-permissions");
  const mounted = await mountComposable(() => useSessionPermissions(() => sessionId.value));
  await flushAll();
  return { ...mounted, sessionId };
}

describe("useSessionPermissions", () => {
  beforeEach(() => {
    subscribeV2Mock.mockReset();
    getMock.mockReset();
    postMock.mockReset();
    listeners.clear();
    reconnectCallbacks.length = 0;
    subscribeV2Mock.mockImplementation((topic: string, _onSnapshot: unknown, onEvent: OnEvent) => {
      listeners.set(topic, onEvent);
      return () => listeners.delete(topic);
    });
    getMock.mockResolvedValue({ data: [], response: { ok: true } });
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("loads what waits when the session opens, and again after a reconnect", async () => {
    getMock.mockResolvedValue({ data: [ask], response: { ok: true } });
    const { result, wrapper } = await mountPermissions(shallowRef("s-load"));

    expect(getMock).toHaveBeenCalledWith("/api/sessions/{id}/permissions", { params: { path: { id: "s-load" } } });
    expect(result.asks.value.map((a) => a.title)).toEqual(["git push origin main"]);

    getMock.mockResolvedValue({ data: [], response: { ok: true } });
    reconnectCallbacks.forEach((callback) => callback());
    await flushAll();
    expect(result.asks.value).toEqual([]);
    wrapper.unmount();
  });

  it("adds an ask when one is asked and drops it when it's answered or gone", async () => {
    const { result, wrapper } = await mountPermissions(shallowRef("s-events"));
    const onEvent = listeners.get("session:s-events")!;

    onEvent(asked({ ...ask, sessionId: "s-events" }));
    onEvent(asked({ ...ask, sessionId: "s-events" }));
    expect(result.asks.value).toHaveLength(1);
    expect(result.asks.value[0]).toMatchObject({ kind: "shell", always: ["git push *"], subagent: null });

    onEvent(replied("per_1"));
    expect(result.asks.value).toEqual([]);
    wrapper.unmount();
  });

  it("ignores an ask it can't read", async () => {
    const { result, wrapper } = await mountPermissions(shallowRef("s-bad"));

    listeners.get("session:s-bad")!(asked({ id: "per_2" }));

    expect(result.asks.value).toEqual([]);
    wrapper.unmount();
  });

  it("answers on the session whose harness asked, and the card goes", async () => {
    const { result, wrapper } = await mountPermissions(shallowRef("s-parent"));
    listeners.get("session:s-parent")!(asked({ ...ask, sessionId: "s-child", subagent: "subagent" }));
    postMock.mockResolvedValue({ response: { ok: true, status: 204 } });

    await result.answer(result.asks.value[0], "reject", "  Not yet  ");

    expect(postMock).toHaveBeenCalledWith("/api/sessions/{id}/permissions/{requestId}", {
      params: { path: { id: "s-child", requestId: "per_1" } },
      body: { reply: "reject", message: "Not yet" },
    });
    expect(result.asks.value).toEqual([]);
    wrapper.unmount();
  });

  it("keeps the card and says why when the answer didn't reach the agent", async () => {
    const { result, wrapper } = await mountPermissions(shallowRef("s-fail"));
    listeners.get("session:s-fail")!(asked({ ...ask, sessionId: "s-fail" }));
    postMock.mockResolvedValue({ error: { error: "The harness is restarting." }, response: { ok: false, status: 500 } });

    await expect(result.answer(result.asks.value[0], "once")).rejects.toThrow("The harness is restarting.");
    expect(result.asks.value).toHaveLength(1);
    wrapper.unmount();
  });

  it("follows the session it's shown for", async () => {
    const sessionId = shallowRef("s-a");
    const { wrapper } = await mountPermissions(sessionId);

    sessionId.value = "s-b";
    await flushAll();

    expect(listeners.has("session:s-a")).toBe(false);
    expect(listeners.has("session:s-b")).toBe(true);
    wrapper.unmount();
  });
});
