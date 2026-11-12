import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { ref } from "vue";
import type { DomainEvent } from "@/lib/domain-events";
import { flushAll, mountComposable } from "./test-utils";

const { onGlobalEventMock, navigateMock, getPreferencesMock } = vi.hoisted(() => ({
  onGlobalEventMock: vi.fn(),
  navigateMock: vi.fn(),
  getPreferencesMock: vi.fn(),
}));

vi.mock("@/api/client", () => ({ api: { GET: getPreferencesMock, PUT: vi.fn() } }));

vi.mock("@/composables/use-signalr-socket", () => ({ onGlobalEvent: onGlobalEventMock }));

const locationRef = ref({ pathname: "/" });

vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate: navigateMock }), useLocation: () => locationRef }));

/** What the router does when you go to a path: the address and the reactive location both change. */
function goTo(pathname: string): void {
  window.history.replaceState({}, "", pathname);
  locationRef.value = { pathname };
}

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
  static closed = 0;
  close = () => { FakeNotification.closed++; };

  constructor(title: string, options: NotificationOptions = {}) {
    shown.push({ title, options });
  }
}

async function mountNotifications(enabled = true) {
  getPreferencesMock.mockResolvedValue({ data: enabled ? { DesktopNotifications: "true" } : {} });
  const { useSessionNotifications } = await import("@/composables/use-session-notifications");
  const mounted = await mountComposable(() => useSessionNotifications());
  wrappers.push(mounted.wrapper);
  return mounted;
}

const wrappers: { unmount(): void }[] = [];

describe("useSessionNotifications", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    onGlobalEventMock.mockReset();
    navigateMock.mockReset();
    getPreferencesMock.mockReset();
    shown.length = 0;
    permission = "granted";
    hasFocus = true;

    onGlobalEventMock.mockImplementation((_machine: unknown, _topic: string, listener: (event: DomainEvent) => void) => {
      handler = listener;
      return () => { handler = null; };
    });

    vi.stubGlobal("Notification", FakeNotification);
    Object.defineProperty(FakeNotification, "permission", { get: () => permission, configurable: true });
    vi.spyOn(document, "hasFocus").mockImplementation(() => hasFocus);
    goTo("/");
  });

  afterEach(() => {
    for (const wrapper of wrappers.splice(0)) wrapper.unmount();
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
    goTo("/sessions/s1");

    handler?.(notificationEvent("s1"));
    await flushAll();

    expect(shown).toHaveLength(0);
  });

  it("still speaks up about the open session when the window is in the background", async () => {
    await mountNotifications();
    goTo("/sessions/s1");
    hasFocus = false;

    handler?.(notificationEvent("s1"));
    await flushAll();

    expect(shown).toHaveLength(1);
  });

  it("takes down its own notification when you open that session", async () => {
    FakeNotification.closed = 0;
    await mountNotifications();

    handler?.(notificationEvent("s1"));
    await flushAll();
    goTo("/sessions/s2");
    await flushAll();
    expect(FakeNotification.closed).toBe(0);

    goTo("/sessions/s1");
    await flushAll();
    expect(FakeNotification.closed).toBe(1);
  });

  it("takes down a pushed notification for the session once it is open and in front", async () => {
    const { useServiceWorker } = await import("@/composables/use-service-worker");
    const closeMine = vi.fn();
    const closeTheirs = vi.fn();
    const getNotifications = vi.fn().mockResolvedValue([
      { tag: "machine-1:s1", close: closeMine },
      { tag: "s1", close: closeMine },
      { tag: "machine-1:s2", close: closeTheirs },
    ]);
    useServiceWorker().registration.value = { getNotifications } as unknown as ServiceWorkerRegistration;
    await mountNotifications();

    hasFocus = false;
    goTo("/sessions/s1");
    await flushAll();
    expect(closeMine).not.toHaveBeenCalled();

    hasFocus = true;
    window.dispatchEvent(new Event("focus"));
    await flushAll();

    expect(closeMine).toHaveBeenCalledTimes(2);
    expect(closeTheirs).not.toHaveBeenCalled();
    useServiceWorker().registration.value = null;
  });

  it("ignores other events on the sessions topic", async () => {
    await mountNotifications();

    handler?.({ type: "activity_status", payload: { sessionId: "s1", activityStatus: "idle" } } as DomainEvent);
    await flushAll();

    expect(shown).toHaveLength(0);
  });
});
