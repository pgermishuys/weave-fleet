/// <reference lib="webworker" />
/**
 * Fleet's service worker, hand-written and built on its own to /sw.js (vite.sw.config.ts). It shows Fleet's
 * pushes, opens the right page when one is tapped, and re-subscribes when the browser rotates the subscription.
 *
 * It caches nothing: there is no fetch handler, so every request goes to the network as if it weren't here.
 * It never imports Vue or touches `window`.
 */
import { readCredentials } from "@/lib/device-credentials";
import { answerFromNotification } from "@/lib/push/notification-action";
import { answeredNotification, notificationFor, type NotificationData } from "@/lib/push/notification-options";
import { parsePushPayload } from "@/lib/push/payload";

const sw = self as unknown as ServiceWorkerGlobalScope;

sw.addEventListener("install", () => {
  void sw.skipWaiting();
});

sw.addEventListener("activate", (event) => {
  event.waitUntil(sw.clients.claim());
});

sw.addEventListener("push", (event) => {
  event.waitUntil(showPush(event));
});

async function showPush(event: PushEvent): Promise<void> {
  let data: unknown = null;
  try {
    data = event.data?.json();
  } catch {
    data = null;
  }
  const payload = parsePushPayload(data);
  if (!payload) return;

  const maxActions = (Notification as unknown as { maxActions?: number }).maxActions ?? 0;
  const { title, options } = notificationFor(payload, maxActions);
  await sw.registration.showNotification(title, options as NotificationOptions);
}

sw.addEventListener("notificationclick", (event) => {
  event.notification.close();
  const data = event.notification.data as NotificationData | undefined;
  if (event.action && data) {
    event.waitUntil(answer(event.action, data));
    return;
  }
  event.waitUntil(openUrl(data?.url ?? "/phone"));
});

/** Allow once or Deny pressed on the notification (Android): answer without opening Fleet, then say so. */
async function answer(action: string, data: NotificationData): Promise<void> {
  const result = await answerFromNotification(action, data, await readCredentials(), sw.location.origin, (input, init) => fetch(input, init));
  if (result.kind === "open") {
    await openUrl(result.url);
    return;
  }
  const { title, options } = answeredNotification(data, data.machineName ?? "", result.allowed);
  await sw.registration.showNotification(title, options as NotificationOptions);
}

/** Focuses a Fleet window already open on this origin and sends it to `url`, or opens one. */
async function openUrl(url: string): Promise<void> {
  const target = new URL(url, sw.location.origin);
  if (target.origin !== sw.location.origin) return;

  const windows = await sw.clients.matchAll({ type: "window", includeUncontrolled: true });
  const open = windows.find((client) => new URL(client.url).origin === sw.location.origin);
  if (open) {
    await open.focus();
    open.postMessage({ type: "navigate", url: `${target.pathname}${target.search}` });
    return;
  }
  await sw.clients.openWindow(target.href);
}

sw.addEventListener("pushsubscriptionchange", (event) => {
  const change = event as Event & { oldSubscription?: PushSubscription | null; waitUntil(promise: Promise<unknown>): void };
  change.waitUntil(resubscribe(change.oldSubscription ?? null));
});

/** The browser dropped or rotated the subscription: make a new one and tell home, carrying the old one's settings. */
async function resubscribe(old: PushSubscription | null): Promise<void> {
  const credentials = await readCredentials();
  const headers: Record<string, string> = { "Content-Type": "application/json" };
  if (credentials) headers.Authorization = `Bearer ${credentials.token}`;
  const init: RequestInit = { headers, credentials: credentials ? "omit" : "include" };

  const vapid = await fetch("/api/push/vapid", init);
  if (!vapid.ok) return;
  const { publicKey } = await vapid.json() as { publicKey: string };

  const subscription = await sw.registration.pushManager.subscribe({
    userVisibleOnly: true,
    applicationServerKey: urlBase64ToUint8Array(publicKey),
  });
  const json = subscription.toJSON();
  await fetch("/api/push/subscriptions", {
    ...init,
    method: "PUT",
    body: JSON.stringify({
      endpoint: json.endpoint,
      keys: json.keys,
      previousEndpoint: old?.endpoint ?? null,
    }),
  });
}

function urlBase64ToUint8Array(value: string): Uint8Array<ArrayBuffer> {
  const padded = (value + "=".repeat((4 - (value.length % 4)) % 4)).replace(/-/g, "+").replace(/_/g, "/");
  const raw = atob(padded);
  const bytes = new Uint8Array(new ArrayBuffer(raw.length));
  for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
  return bytes;
}
