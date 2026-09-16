import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import type { DomainEvent } from "@/lib/domain-events";
import { flushAll, mountComposable } from "./test-utils";

const { onGlobalEventMock, navigateMock, getPreferencesMock } = vi.hoisted(() => ({
  onGlobalEventMock: vi.fn(),
  navigateMock: vi.fn(),
  getPreferencesMock: vi.fn(),
}));

vi.mock("@/api/client", () => ({ api: { GET: getPreferencesMock, PUT: vi.fn() } }));

vi.mock("@/composables/use-signalr-socket", () => ({ onGlobalEvent: onGlobalEventMock }));

vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate: navigateMock }) }));

/** The handler the composable registered for the global sessions topic. */
let handler: ((event: DomainEvent) => void) | null = null;

/** Every notification the composable constructed, newest last. */
const shown: { title: string; options: NotificationOptions }[] = [];

let permission: NotificationPermission = "granted";
let hasFocus = true;

function notificationEvent(sessionId = "s1", reason: "needs_you" | "finished" = "needs_you"): DomainEvent {
  return {
    type: "session_notification",
    payload: { sessionId, reason, title: "Make the dashboard true", body: "Waiting on your answer." },
  };
}

class FakeNotification {
  static permission: NotificationPermission = "granted";
  static requestPermission = vi.fn();
  onclick: (() => void) | null = null;
  close = vi.fn();

  constructor(title: string, options: NotificationOptions = {}) {
    shown.push({ title, options });
  }
}

async function mountNotifications(enabled = true) {
  getPreferencesMock.mockResolvedValue({ data: enabled ? { DesktopNotifications: "true" } : {} });
  const { useSessionNotifications } = await import("@/composables/use-session-notifications");
  return mountComposable(() => useSessionNotifications());
}

describe("useSessionNotifications", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    onGlobalEventMock.mockReset();
    navigateMock.mockReset();
    getPreferencesMock.mockReset();
    shown.length = 0;
    permission = "granted";
    hasFocus = true;

    onGlobalEventMock.mockImplementation((_topic: string, listener: (event: DomainEvent) => void) => {
      handler = listener;
      return () => { handler = null; };
    });

    vi.stubGlobal("Notification", FakeNotification);
    Object.defineProperty(FakeNotification, "permission", { get: () => permission, configurable: true });
    vi.spyOn(document, "hasFocus").mockImplementation(() => hasFocus);
    window.history.replaceState({}, "", "/");
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it("shows a notification for a session that needs you", async () => {
    await mountNotifications();

    handler?.(notificationEvent());
    await flushAll();

    expect(shown).toHaveLength(1);
    expect(shown[0].title).toBe("Make the dashboard true");
    expect(shown[0].options.body).toBe("Waiting on your answer.");
    // Tagged so several open tabs collapse into one notification.
    expect(shown[0].options.tag).toBe("s1");
  });

  it("stays quiet when the setting is off", async () => {
    await mountNotifications(false);

    handler?.(notificationEvent());
    await flushAll();

    expect(shown).toHaveLength(0);
  });

  it("stays quiet when the browser has not granted permission", async () => {
    permission = "denied";
    await mountNotifications();

    handler?.(notificationEvent());
    await flushAll();

    expect(shown).toHaveLength(0);
  });

  it("stays quiet about the session this tab is looking at", async () => {
    await mountNotifications();
    window.history.replaceState({}, "", "/sessions/s1");

    handler?.(notificationEvent("s1"));
    await flushAll();

    expect(shown).toHaveLength(0);
  });

  it("still speaks up about the open session when the window is in the background", async () => {
    await mountNotifications();
    window.history.replaceState({}, "", "/sessions/s1");
    hasFocus = false;

    handler?.(notificationEvent("s1"));
    await flushAll();

    expect(shown).toHaveLength(1);
  });

  it("ignores other events on the sessions topic", async () => {
    await mountNotifications();

    handler?.({ type: "activity_status", payload: { sessionId: "s1", activityStatus: "idle" } } as DomainEvent);
    await flushAll();

    expect(shown).toHaveLength(0);
  });
});
