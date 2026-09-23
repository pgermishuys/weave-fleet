import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { watch } from "vue";
import type { DomainEvent } from "@/lib/domain-events";
import type { SessionSnapshot } from "@/lib/session-snapshot";
import { mountComposable } from "./test-utils";

const { subscribeV2Mock } = vi.hoisted(() => ({ subscribeV2Mock: vi.fn() }));

vi.mock("@/composables/use-weave-socket", () => ({
  useWeaveSocket: () => ({ subscribeV2: subscribeV2Mock }),
  loadSessionHistory: vi.fn(),
}));

vi.mock("@/composables/use-signalr-socket", () => ({
  onGlobalEvent: () => () => undefined,
}));

type Listener = { onSnapshot: (snapshot: SessionSnapshot) => void; onEvent: (event: DomainEvent) => void };
let listener: Listener | null = null;

function snapshot(text: string): SessionSnapshot {
  return {
    session: { id: "s1", title: "Streaming", status: "active" },
    messages: [{
      info: {
        id: "m1", sessionID: "s1", role: "assistant", agent: "build", modelID: null, parentID: null,
        time: { created: 1, completed: null }, cost: null, tokens: null,
      },
      parts: [{ id: "p1", sessionID: "s1", messageID: "m1", type: "text", text }],
    }],
    delegations: [],
    activityStatus: "busy",
    lastEventId: null,
    hasMore: false,
    cursor: null,
    isPartial: false,
  };
}

function delta(text: string): DomainEvent {
  return {
    type: "message.part.delta.streamed",
    payload: { sessionID: "s1", messageID: "m1", partID: "p1", field: "text", delta: text },
  } as DomainEvent;
}

async function mountStream() {
  const { useSessionStream } = await import("@/composables/use-session-stream");
  const mounted = await mountComposable(() => useSessionStream("s1"));
  listener!.onSnapshot(snapshot("Hello"));
  const updates = vi.fn();
  watch(mounted.result.messages, updates, { flush: "sync" });
  const text = () => {
    const part = mounted.result.messages.value[0]?.parts[0];
    return part?.type === "text" ? part.text : undefined;
  };
  return { ...mounted, updates, text };
}

describe("useSessionStream frame batching", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "requestAnimationFrame", "cancelAnimationFrame"] });
    subscribeV2Mock.mockReset();
    subscribeV2Mock.mockImplementation((_topic: string, onSnapshot: Listener["onSnapshot"], onEvent: Listener["onEvent"]) => {
      listener = { onSnapshot, onEvent };
      return () => {
        listener = null;
      };
    });
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it("applies the deltas that arrive within a frame together on the next frame", async () => {
    const { updates, text } = await mountStream();

    listener!.onEvent(delta(", "));
    listener!.onEvent(delta("world"));
    listener!.onEvent(delta("!"));
    expect(text()).toBe("Hello");

    vi.advanceTimersToNextFrame();
    expect(text()).toBe("Hello, world!");
    expect(updates).toHaveBeenCalledTimes(1);
  });

  it("skips an event it can't apply and still applies the rest of the frame", async () => {
    const { text } = await mountStream();
    vi.spyOn(console, "error").mockImplementation(() => undefined);

    listener!.onEvent(delta(","));
    // The server's tool-result part: camelCase ids, no messageID.
    listener!.onEvent({
      type: "message.part.updated",
      payload: { part: { type: "tool-result", id: "", messageId: "m1", sessionId: "s1", callId: "c1", content: "done" } },
    } as unknown as DomainEvent);
    listener!.onEvent(delta(" world"));

    vi.advanceTimersToNextFrame();
    expect(text()).toBe("Hello, world");
  });

  it("still applies them when no frame comes, as in a hidden tab", async () => {
    vi.spyOn(window, "requestAnimationFrame").mockImplementation(() => 1);
    vi.spyOn(window, "cancelAnimationFrame").mockImplementation(() => undefined);
    const { text } = await mountStream();

    listener!.onEvent(delta(" there"));
    vi.advanceTimersByTime(100);
    expect(text()).toBe("Hello there");
  });

  it("drops the waiting events when a snapshot replaces the state they'd change", async () => {
    const { text } = await mountStream();

    listener!.onEvent(delta(" again"));
    listener!.onSnapshot(snapshot("Hello again"));
    vi.advanceTimersToNextFrame();
    expect(text()).toBe("Hello again");
  });

  it("drops them when the view leaves the session", async () => {
    const { wrapper, updates } = await mountStream();

    listener!.onEvent(delta(" late"));
    wrapper.unmount();
    vi.advanceTimersByTime(200);
    expect(updates).not.toHaveBeenCalled();
  });
});
