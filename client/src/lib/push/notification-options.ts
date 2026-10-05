/**
 * How a Fleet push shows as a system notification. No Vue and no `window`: the service worker imports this.
 */
import type { PushPayloadV1 } from "@/lib/push/payload";

/** What the service worker keeps on a notification to act on a tap. Never a token. */
export interface NotificationData {
  url: string;
  machineId: string;
  machineName?: string;
  sessionId: string;
  kind: PushPayloadV1["kind"];
  requestId?: string;
}

export interface NotificationAction {
  action: string;
  title: string;
}

/** The subset of `NotificationOptions` Fleet sets; `actions` exists only where the platform shows buttons. */
export interface FleetNotificationOptions {
  body: string;
  tag: string;
  data: NotificationData;
  icon: string;
  badge: string;
  /** A repeat with the same tag buzzes again: a new ask is worth noticing. */
  renotify: boolean;
  requireInteraction: boolean;
  actions?: NotificationAction[];
}

export const ALLOW_ONCE_ACTION = "allow-once";
export const DENY_ACTION = "deny";

/**
 * Buttons for a notification: Allow once and Deny on a permission ask, where the platform shows buttons (Android;
 * iOS shows none, and a tap opens the ask instead). The service worker answers them with the phone's own key.
 */
export function actionsFor(payload: PushPayloadV1, maxActions: number): NotificationAction[] {
  if (payload.kind !== "permission" || !payload.requestId || maxActions < 2) return [];
  return [
    { action: ALLOW_ONCE_ACTION, title: "Allow once" },
    { action: DENY_ACTION, title: "Deny" },
  ];
}

/** The notification that replaces an answered ask, under the same tag. Tapping it opens the Answered page. */
export function answeredNotification(data: NotificationData, machineName: string, allowed: boolean): { title: string; options: FleetNotificationOptions } {
  const where = machineName || "The machine";
  return {
    title: allowed ? `Allowed — ${where} carries on` : `Denied — ${where} was told`,
    options: {
      body: allowed ? "Allowed once." : "The agent was told no.",
      tag: `${data.machineId}:${data.sessionId}`,
      data: {
        ...data,
        url: `/phone/answered?machine=${encodeURIComponent(data.machineId)}&session=${encodeURIComponent(data.sessionId)}&reply=${allowed ? "once" : "reject"}`,
      },
      icon: "/icons/icon-192.png",
      badge: "/icons/icon-192.png",
      renotify: false,
      requireInteraction: false,
    },
  };
}

/** The title and options for `payload`. `maxActions` is `Notification.maxActions` (0 on iOS). */
export function notificationFor(payload: PushPayloadV1, maxActions = 0): { title: string; options: FleetNotificationOptions } {
  const asks = payload.kind === "permission" || payload.kind === "question";
  const actions = actionsFor(payload, maxActions);
  const where = payload.machineName ? `${payload.machineName} · ` : "";
  return {
    title: payload.title,
    options: {
      body: `${where}${payload.body}`.trim(),
      tag: payload.tag,
      data: {
        url: payload.url,
        machineId: payload.machineId,
        machineName: payload.machineName,
        sessionId: payload.sessionId,
        kind: payload.kind,
        ...(payload.requestId ? { requestId: payload.requestId } : {}),
      },
      icon: "/icons/icon-192.png",
      badge: "/icons/icon-192.png",
      renotify: true,
      requireInteraction: asks,
      ...(actions.length ? { actions } : {}),
    },
  };
}
