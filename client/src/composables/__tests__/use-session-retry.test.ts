import { beforeEach, describe, expect, it, vi } from "vitest";
import type { DomainEvent } from "@/lib/domain-events";
import { flushAll, mountComposable } from "./test-utils";

const { subscribeV2Mock, getMock, postMock, deleteMock, reconnectCallbacks } = vi.hoisted(() => ({
  subscribeV2Mock: vi.fn(),
  getMock: vi.fn(),
  postMock: vi.fn(),
  deleteMock: vi.fn(),
  reconnectCallbacks: [] as (() => void)[],
}));

vi.mock("@/api/client", () => ({ api: { GET: getMock, POST: postMock, DELETE: deleteMock } }));
vi.mock("@/composables/use-weave-socket", () => ({
  useWeaveSocket: () => ({ subscribeV2: subscribeV2Mock }),
  onReconnect: (_machine: unknown, callback: () => void) => {
    reconnectCallbacks.push(callback);
    return () => reconnectCallbacks.splice(reconnectCallbacks.indexOf(callback), 1);
  },
}));

type OnEvent = (event: DomainEvent) => void;
const listeners = new Map<string, OnEvent>();
let sessionCounter = 0;

const retry = { dueAt: "2026-10-09T12:00:00Z", attempt: 2, kind: "usage_limit", reason: "Window used up", providerSaid: true };

function retryEvent(sessionId: string, value: unknown): DomainEvent {
  return { type: "session.retry", payload: { sessionId, retry: value } } as DomainEvent;
}

async function mountRetry() {
  // Retries are kept per session id at module level, so every test uses a session of its own.
  const sessionId = `retry-session-${++sessionCounter}`;
  const { useSessionRetry } = await import("@/composables/use-session-retry");
  const mounted = await mountComposable(() => useSessionRetry(sessionId));
  return { ...mounted, sessionId, push: (event: DomainEvent) => listeners.get(`session:${sessionId}`)?.(event) };
}

describe("useSessionRetry", () => {
  beforeEach(() => {
    subscribeV2Mock.mockReset().mockImplementation((topic: string, _onSnapshot: unknown, onEvent: OnEvent) => {
      listeners.set(topic, onEvent);
      return () => listeners.delete(topic);
    });
    getMock.mockReset().mockResolvedValue({ data: undefined, response: { ok: true, status: 204 } });
    postMock.mockReset();
    deleteMock.mockReset();
    reconnectCallbacks.length = 0;
    listeners.clear();
  });

  it("has no retry when the session has none waiting", async () => {
    const { result } = await mountRetry();
    expect(result.retry.value).toBeNull();
  });

  it("loads the waiting retry when the session opens", async () => {
    getMock.mockResolvedValue({ data: retry, response: { ok: true, status: 200 } });

    const { result } = await mountRetry();

    expect(result.retry.value).toMatchObject({ attempt: 2, kind: "usage_limit", providerSaid: true });
  });

  it("follows session.retry events, including the one that takes it away", async () => {
    const { result, sessionId, push } = await mountRetry();

    push(retryEvent(sessionId, retry));
    expect(result.retry.value?.reason).toBe("Window used up");

    push(retryEvent(sessionId, null));
    expect(result.retry.value).toBeNull();
  });

  it("ignores another session's retry and other events", async () => {
    const { result, push } = await mountRetry();

    push(retryEvent("some-other-session", retry));
    push({ type: "session.queue", payload: { sessionId: "x", items: [] } } as DomainEvent);

    expect(result.retry.value).toBeNull();
  });

  it("loads again after a reconnect and stops listening when unmounted", async () => {
    const { wrapper, sessionId } = await mountRetry();
    expect(getMock).toHaveBeenCalledTimes(1);

    reconnectCallbacks[0]?.();
    await flushAll();
    expect(getMock).toHaveBeenCalledTimes(2);

    wrapper.unmount();
    expect(listeners.has(`session:${sessionId}`)).toBe(false);
    expect(reconnectCallbacks).toHaveLength(0);
  });

  it("clears the retry once Fleet sends it now or calls it off", async () => {
    const { result, sessionId, push } = await mountRetry();
    push(retryEvent(sessionId, retry));
    postMock.mockResolvedValue({ response: { ok: true, status: 200 } });
    deleteMock.mockResolvedValue({ response: { ok: true, status: 204 } });

    expect(await result.sendNow()).toBe(true);
    expect(result.retry.value).toBeNull();

    push(retryEvent(sessionId, retry));
    expect(await result.cancel()).toBe(true);
    expect(result.retry.value).toBeNull();
  });

  it("says so when Fleet refuses, and loads again", async () => {
    const { result, sessionId, push } = await mountRetry();
    push(retryEvent(sessionId, retry));
    postMock.mockResolvedValue({ response: { ok: false, status: 500 } });

    expect(await result.sendNow()).toBe(false);
    expect(result.error.value).toBe("Fleet couldn't try again now (HTTP 500).");
  });
});
