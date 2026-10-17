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

const interruption = { at: "2026-10-17T14:02:00Z", waiting: false, lastCall: "bash: bun run build", changedFiles: 3 };

function interruptedEvent(sessionId: string, value: unknown): DomainEvent {
  return { type: "session.interrupted", payload: { sessionId, interruption: value } } as DomainEvent;
}

async function mountInterruption() {
  // Interruptions are kept per session id at module level, so every test uses a session of its own.
  const sessionId = `lumen-site-session-${++sessionCounter}`;
  const { useSessionInterruption, interruptionOf } = await import("@/composables/use-session-interruption");
  const mounted = await mountComposable(() => useSessionInterruption(sessionId));
  return { ...mounted, sessionId, interruptionOf, push: (event: DomainEvent) => listeners.get(`session:${sessionId}`)?.(event) };
}

describe("useSessionInterruption", () => {
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

  it("has no interruption on a 204", async () => {
    const { result } = await mountInterruption();
    expect(result.interruption.value).toBeNull();
  });

  it("loads the interruption on a 200 when the session opens", async () => {
    getMock.mockResolvedValue({ data: interruption, response: { ok: true, status: 200 } });
    const { result, interruptionOf, sessionId } = await mountInterruption();

    expect(result.interruption.value).toMatchObject({ lastCall: "bash: bun run build", changedFiles: 3 });
    expect(interruptionOf(sessionId)?.waiting).toBe(false);
  });

  it("follows session.interrupted events, including the one that clears it", async () => {
    const { result, sessionId, push } = await mountInterruption();

    push(interruptedEvent(sessionId, interruption));
    expect(result.interruption.value?.at).toBe(interruption.at);

    push(interruptedEvent(sessionId, null));
    expect(result.interruption.value).toBeNull();
  });

  it("ignores another session's event", async () => {
    const { result, push } = await mountInterruption();
    push(interruptedEvent("some-other-session", interruption));
    expect(result.interruption.value).toBeNull();
  });

  it("loads again after a reconnect", async () => {
    await mountInterruption();
    reconnectCallbacks[0]?.();
    await flushAll();
    expect(getMock).toHaveBeenCalledTimes(2);
  });

  it("continues through the endpoint and clears the interruption", async () => {
    const { result, sessionId, push } = await mountInterruption();
    push(interruptedEvent(sessionId, interruption));
    postMock.mockResolvedValue({ response: { ok: true, status: 202 } });

    expect(await result.continueSession()).toBe(true);
    expect(postMock).toHaveBeenCalledWith("/api/sessions/{id}/interruption/continue", { params: { path: { id: sessionId } } });
    expect(result.interruption.value).toBeNull();
  });

  it("dismisses through the endpoint and clears the interruption", async () => {
    const { result, sessionId, push } = await mountInterruption();
    push(interruptedEvent(sessionId, interruption));
    deleteMock.mockResolvedValue({ response: { ok: true, status: 204 } });

    expect(await result.dismiss()).toBe(true);
    expect(deleteMock).toHaveBeenCalledWith("/api/sessions/{id}/interruption", { params: { path: { id: sessionId } } });
    expect(result.interruption.value).toBeNull();
  });

  it("says so when Fleet refuses", async () => {
    const { result, sessionId, push } = await mountInterruption();
    push(interruptedEvent(sessionId, interruption));
    postMock.mockResolvedValue({ response: { ok: false, status: 500 } });

    expect(await result.continueSession()).toBe(false);
    expect(result.error.value).toBe("Fleet couldn't continue the session (HTTP 500).");
  });
});
