import { beforeEach, describe, expect, it, vi } from "vitest";
import type { DomainEvent } from "@/lib/domain-events";
import { mountComposable } from "./test-utils";

const { onGlobalEventMock, onReconnectMock, store } = vi.hoisted(() => ({
  onGlobalEventMock: vi.fn(),
  onReconnectMock: vi.fn(),
  store: {
    ensureHeaderLinksLoaded: vi.fn(),
    reloadHeaderLinks: vi.fn(),
    applyPushed: vi.fn(),
  },
}));

vi.mock("@/composables/use-signalr-socket", () => ({ onGlobalEvent: onGlobalEventMock, onReconnect: onReconnectMock }));
vi.mock("@/stores/smart-links", () => ({ useSmartLinksStore: () => store }));

let handler: ((event: DomainEvent) => void) | null = null;
let reconnect: (() => void) | null = null;
let stopEvents: ReturnType<typeof vi.fn>;
let stopReconnect: ReturnType<typeof vi.fn>;

function send(type: string, payload: unknown): void {
  handler?.({ type, payload } as unknown as DomainEvent);
}

async function mountUpdates() {
  const { useSmartLinkUpdates } = await import("@/composables/use-smart-link-updates");
  return mountComposable(() => useSmartLinkUpdates());
}

describe("useSmartLinkUpdates", () => {
  beforeEach(() => {
    handler = null;
    reconnect = null;
    stopEvents = vi.fn();
    stopReconnect = vi.fn();
    store.ensureHeaderLinksLoaded.mockReset().mockResolvedValue(undefined);
    store.reloadHeaderLinks.mockReset().mockResolvedValue(undefined);
    store.applyPushed.mockReset();
    onGlobalEventMock.mockReset().mockImplementation((_machine: unknown, topic: string, callback: (event: DomainEvent) => void) => {
      expect(topic).toBe("sessions");
      handler = callback;
      return stopEvents;
    });
    onReconnectMock.mockReset().mockImplementation((_machine: unknown, callback: () => void) => {
      reconnect = callback;
      return stopReconnect;
    });
  });

  it("loads every session's header links when mounted", async () => {
    await mountUpdates();
    expect(store.ensureHeaderLinksLoaded).toHaveBeenCalledOnce();
  });

  it("applies a pushed link", async () => {
    await mountUpdates();
    const wire = { id: "l1", sessionId: "s1", url: "https://example.test/pull/7" };

    send("smart_link.updated", wire);

    expect(store.applyPushed).toHaveBeenCalledWith(wire);
  });

  it("ignores other events and links without an id or a session", async () => {
    await mountUpdates();

    send("session_tokens", { id: "l1", sessionId: "s1" });
    send("smart_link.updated", { sessionId: "s1" });
    send("smart_link.updated", { id: "l1" });
    send("smart_link.updated", undefined);

    expect(store.applyPushed).not.toHaveBeenCalled();
  });

  it("loads the links again after a reconnect", async () => {
    await mountUpdates();
    reconnect?.();
    expect(store.reloadHeaderLinks).toHaveBeenCalledOnce();
  });

  it("stops listening when unmounted", async () => {
    const { wrapper } = await mountUpdates();
    wrapper.unmount();
    expect(stopEvents).toHaveBeenCalledOnce();
    expect(stopReconnect).toHaveBeenCalledOnce();
  });
});
