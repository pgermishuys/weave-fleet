/**
 * How a Fleet push shows as a system notification. No Vue and no `window`: the service worker imports this.
 */
import type { PushPayloadV1 } from "@/lib/push/payload";

/** What the service worker keeps on a notification to act on a tap. Never a token. */
export interface NotificationData {
  url: string;
  machineId: string;
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

/** Buttons for a notification. Filled in for permission asks where the platform shows them (Android). */
export function actionsFor(payload: PushPayloadV1, maxActions: number): NotificationAction[] {
  void payload;
  void maxActions;
  return [];
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
