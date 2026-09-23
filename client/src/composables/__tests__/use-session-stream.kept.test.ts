import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { shallowRef } from "vue";
import type { DomainEvent } from "@/lib/domain-events";
import type { SessionSnapshot } from "@/lib/session-snapshot";
import { useSessionsStore } from "@/stores/sessions";
import { mountComposable } from "./test-utils";

const { subscribeV2Mock, loadSessionHistoryMock } = vi.hoisted(() => ({
  subscribeV2Mock: vi.fn(),
  loadSessionHistoryMock: vi.fn(),
}));

vi.mock("@/composables/use-weave-socket", () => ({
  useWeaveSocket: () => ({ subscribeV2: subscribeV2Mock }),
  loadSessionHistory: loadSessionHistoryMock,
}));

vi.mock("@/composables/use-signalr-socket", () => ({
  onGlobalEvent: () => () => undefined,
}));

type Listener = { onSnapshot: (snapshot: SessionSnapshot) => void; onEvent: (event: DomainEvent) => void };
const listeners = new Map<string, Listener>();

function message(sessionId: string, id: string, text: string): SessionSnapshot["messages"][number] {
  return {
    info: {
      id, sessionID: sessionId, role: "assistant", agent: "build", modelID: null, parentID: null,
      time: { created: Number(id.replace(/\D/g, "")), completed: null }, cost: null, tokens: null,
    },
    parts: [{ id: `${id}-p`, sessionID: sessionId, messageID: id, type: "text", text }],
  };
}

function snapshot(sessionId: string, texts: string[], activityStatus = "idle"): SessionSnapshot {
  return {
    session: { id: sessionId, title: sessionId, status: "active" },
    messages: texts.map((text, index) => message(sessionId, `m${index + 1}`, text)),
    delegations: [],
    activityStatus,
    lastEventId: null,
    hasMore: true,
    cursor: `${sessionId}-cursor`,
    isPartial: false,
  };
}

async function mountStream(sessionId = shallowRef("a")) {
  const { useSessionStream } = await import("@/composables/use-session-stream");
  const mounted = await mountComposable(() => useSessionStream(sessionId));
  const texts = () => mounted.result.messages.value.map((m) => (m.parts[0]?.type === "text" ? m.parts[0].text : ""));
  return { ...mounted, sessionId, texts };
}

describe("useSessionStream kept sessions", () => {
  beforeEach(async () => {
    setActivePinia(createPinia());
    listeners.clear();
    subscribeV2Mock.mockReset();
    loadSessionHistoryMock.mockReset();
    subscribeV2Mock.mockImplementation((topic: string, onSnapshot: Listener["onSnapshot"], onEvent: Listener["onEvent"]) => {
      listeners.set(topic, { onSnapshot, onEvent });
      return () => listeners.delete(topic);
    });
    const { _resetKeptStreamsForTesting } = await import("@/composables/use-session-stream");
    _resetKeptStreamsForTesting();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("shows a session left earlier at once, before its fresh snapshot arrives", async () => {
    const { sessionId, texts, result } = await mountStream();
    listeners.get("session:a")!.onSnapshot(snapshot("a", ["Hello from A"]));

    sessionId.value = "b";
    await Promise.resolve();
    expect(texts()).toEqual([]);
    listeners.get("session:b")!.onSnapshot(snapshot("b", ["Hello from B"]));

    sessionId.value = "a";
    await Promise.resolve();
    expect(texts()).toEqual(["Hello from A"]);
    expect(result.isLoading.value).toBe(true);
    expect(subscribeV2Mock).toHaveBeenLastCalledWith("session:a", expect.any(Function), expect.any(Function), expect.any(Function));
  });

  it("takes the fresh snapshot over the kept state, keeping the messages that didn't change", async () => {
    const { sessionId, texts, result } = await mountStream();
    listeners.get("session:a")!.onSnapshot(snapshot("a", ["First", "Second"]));
    const [keptFirst] = result.messages.value;

    sessionId.value = "b";
    await Promise.resolve();
    sessionId.value = "a";
    await Promise.resolve();
    listeners.get("session:a")!.onSnapshot(snapshot("a", ["First", "Second, edited", "Third"]));

    expect(texts()).toEqual(["First", "Second, edited", "Third"]);
    expect(result.messages.value[0]).toBe(keptFirst);
    expect(result.isLoading.value).toBe(false);
  });

  it("shows the kept state with the status the session list has now", async () => {
    const { sessionId, result } = await mountStream();
    listeners.get("session:a")!.onSnapshot(snapshot("a", ["Working on it"], "busy"));
    sessionId.value = "b";
    await Promise.resolve();

    // A finished while you were away; the session list heard about it.
    useSessionsStore().sessions.push({ session: { id: "a" }, activityStatus: "idle" } as never);
    sessionId.value = "a";
    await Promise.resolve();

    expect(result.sessionStatus.value).toBe("idle");
  });

  it("doesn't load older messages from a kept cursor before the fresh snapshot", async () => {
    const { sessionId, result } = await mountStream();
    listeners.get("session:a")!.onSnapshot(snapshot("a", ["Hello"]));
    sessionId.value = "b";
    await Promise.resolve();
    sessionId.value = "a";
    await Promise.resolve();

    result.loadOlder();
    expect(loadSessionHistoryMock).not.toHaveBeenCalled();
  });

  it("keeps nothing for a session whose snapshot never arrived", async () => {
    const { sessionId, texts } = await mountStream();
    sessionId.value = "b";
    await Promise.resolve();
    sessionId.value = "a";
    await Promise.resolve();

    expect(texts()).toEqual([]);
  });
});
