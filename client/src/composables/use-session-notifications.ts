import { computed, onUnmounted } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { onGlobalEvent } from "@/composables/use-signalr-socket";
import { usePreferencesStore } from "@/stores/preferences";
import { isSessionNotificationEvent, type DomainEvent } from "@/lib/domain-events";

/** The Settings → Features switch for desktop notifications. Off unless turned on. */
export const DESKTOP_NOTIFICATIONS_PREFERENCE_KEY = "DesktopNotifications";

/** Whether this browser can show desktop notifications at all. */
export function notificationsSupported(): boolean {
  return typeof window !== "undefined" && "Notification" in window;
}

/** What the browser has been told: "granted", "denied", or "default" before it has been asked. */
export function notificationPermission(): NotificationPermission | "unsupported" {
  return notificationsSupported() ? Notification.permission : "unsupported";
}

/**
 * Asks the browser for permission to show notifications, if it hasn't been asked. Returns what it
 * decided. Must be called from a click: browsers ignore the request otherwise.
 */
export async function requestNotificationPermission(): Promise<NotificationPermission | "unsupported"> {
  if (!notificationsSupported()) return "unsupported";
  if (Notification.permission !== "default") return Notification.permission;

  try {
    return await Notification.requestPermission();
  } catch {
    return Notification.permission;
  }
}

/** Whether this tab is looking at a session right now: it's open, the tab is visible and focused. */
function isLookingAt(sessionId: string, pathname: string): boolean {
  return pathname.startsWith(`/sessions/${sessionId}`)
    && document.visibilityState === "visible"
    && document.hasFocus();
}

/**
 * Shows a desktop notification when a session needs you, or finishes, while you're looking somewhere
 * else. The server decides that — it knows which session each tab has open — and sends one event per
 * session; every open tab gets it, so the notification is tagged with the session id and the browser
 * collapses the copies into one. Clicking it brings Fleet forward on that session.
 */
export function useSessionNotifications(): void {
  const preferences = usePreferencesStore();
  preferences.ensureLoaded();
  const router = useRouter();
  const enabled = computed(() => preferences.get(DESKTOP_NOTIFICATIONS_PREFERENCE_KEY, "false") === "true");

  const unsubscribe = onGlobalEvent("sessions", (event: DomainEvent) => {
    if (!isSessionNotificationEvent(event) || !enabled.value) return;
    if (!notificationsSupported() || Notification.permission !== "granted") return;

    const { payload } = event;
    const sessionId = payload.sessionId;
    if (!sessionId) return;

    // The server sent this because no tab was looking; make sure that's still true of this one.
    if (isLookingAt(sessionId, window.location.pathname)) return;

    try {
      const notification = new Notification(payload.title || "Weave Fleet", {
        body: payload.body ?? "",
        tag: sessionId,
      });

      notification.onclick = () => {
        window.focus();
        notification.close();
        void router.navigate({
          to: "/sessions/$id",
          params: { id: sessionId },
          search: { instanceId: undefined, parentSessionId: undefined },
        });
      };
    } catch {
      // Some browsers refuse a constructed Notification (Android wants a service worker). Nothing to do.
    }
  });

  onUnmounted(unsubscribe);
}
