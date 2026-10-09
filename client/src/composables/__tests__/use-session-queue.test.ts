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

function queueEvent(sessionId: string, items: { id: string; kind: string; text: string }[]): DomainEvent {
  return {
    type: "session.queue",
    payload: { sessionId, items: items.map((item) => ({ ...item, createdAt: "2026-10-09T10:00:00Z" })) },
  } as DomainEvent;
}

async function mountQueue() {
  // Queues are kept per session id at module level, so every test uses a session of its own.
  const sessionId = `queue-session-${++sessionCounter}`;
  const { useSessionQueue } = await import("@/composables/use-session-queue");
  const mounted = await mountComposable(() => useSessionQueue(sessionId));
  return { ...mounted, sessionId, push: (event: DomainEvent) => listeners.get(`session:${sessionId}`)?.(event) };
}

describe("useSessionQueue", () => {
  beforeEach(() => {
    subscribeV2Mock.mockReset().mockImplementation((topic: string, _onSnapshot: unknown, onEvent: OnEvent) => {
      listeners.set(topic, onEvent);
      return () => listeners.delete(topic);
    });
    getMock.mockReset().mockResolvedValue({ data: [], response: { ok: true } });
    postMock.mockReset();
    deleteMock.mockReset();
    reconnectCallbacks.length = 0;
    listeners.clear();
  });

  it("loads the queue when the session opens", async () => {
    getMock.mockResolvedValue({ data: [{ id: "q1", kind: "prompt", text: "Then add the tests" }], response: { ok: true } });

    const { result } = await mountQueue();

    expect(result.queue.value).toEqual([{ id: "q1", kind: "prompt", text: "Then add the tests" }]);
  });

  it("replaces the queue with the one a session.queue event carries", async () => {
    const { result, sessionId, push } = await mountQueue();

    push(queueEvent(sessionId, [
      { id: "q1", kind: "prompt", text: "First" },
      { id: "q2", kind: "shell", text: "!git status" },
      { id: "q3", kind: "command", text: "/review" },
    ]));

    expect(result.queue.value.map((item) => [item.id, item.kind])).toEqual([["q1", "prompt"], ["q2", "shell"], ["q3", "command"]]);

    push(queueEvent(sessionId, []));
    expect(result.queue.value).toEqual([]);
  });

  it("ignores another session's queue and other events", async () => {
    const { result, push } = await mountQueue();

    push(queueEvent("some-other-session", [{ id: "q1", kind: "prompt", text: "Not mine" }]));
    push({ type: "session.retry", payload: { sessionId: "x", retry: null } } as DomainEvent);

    expect(result.queue.value).toEqual([]);
  });

  it("loads again after a reconnect and stops listening when unmounted", async () => {
    const { wrapper, sessionId } = await mountQueue();
    expect(getMock).toHaveBeenCalledTimes(1);

    reconnectCallbacks[0]?.();
    await flushAll();
    expect(getMock).toHaveBeenCalledTimes(2);

    wrapper.unmount();
    expect(listeners.has(`session:${sessionId}`)).toBe(false);
    expect(reconnectCallbacks).toHaveLength(0);
  });

  it("queues a message and shows it at once", async () => {
    const { result } = await mountQueue();
    postMock.mockResolvedValue({ data: { id: "q9", kind: "prompt", text: "Run the linter" }, response: { ok: true, status: 200 } });

    const ok = await result.enqueue("Run the linter", { kind: "prompt" });

    expect(ok).toBe(true);
    expect(result.queue.value.map((item) => item.text)).toEqual(["Run the linter"]);
  });

  it("reports why a message couldn't be queued", async () => {
    const { result } = await mountQueue();
    postMock.mockResolvedValue({ error: { error: "The queue is full." }, response: { ok: false, status: 409 } });

    const ok = await result.enqueue("One more", { kind: "prompt" });

    expect(ok).toBe(false);
    expect(result.error.value).toBe("The queue is full.");
  });

  it("takes an item back, and sends one now", async () => {
    const { result, sessionId, push } = await mountQueue();
    push(queueEvent(sessionId, [{ id: "q1", kind: "prompt", text: "A" }, { id: "q2", kind: "prompt", text: "B" }]));
    deleteMock.mockResolvedValue({ response: { ok: true, status: 204 } });
    postMock.mockResolvedValue({ response: { ok: true, status: 200 } });

    await result.remove("q1");
    expect(result.queue.value.map((item) => item.id)).toEqual(["q2"]);

    expect(await result.sendNow("q2")).toBe(true);
    expect(result.queue.value).toEqual([]);
  });
});
