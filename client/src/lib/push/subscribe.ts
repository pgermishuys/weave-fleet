/**
 * Talking to the home machine about this browser's push subscription: subscribe, save choices, read them back, test,
 * remove. The endpoint is a capability URL, so it only ever travels in request bodies. No Vue.
 */
import type { PushKind } from "@/lib/push/payload";

/** What the phone chose, as the server keeps it per subscription. */
export interface PushChoices {
  kinds: PushKind[];
  quietWhenDesk: boolean;
}

export interface SavedSubscription extends PushChoices {
  channel: string;
  createdAt: string;
  lastSuccessAt: string | null;
}

/** How to reach home: a base URL ("" for same origin) and, for a phone, its device token. */
export interface PushTarget {
  baseUrl: string;
  token?: string | null;
}

function init(target: PushTarget, method: string, body?: unknown): RequestInit {
  const headers: Record<string, string> = { "Content-Type": "application/json" };
  if (target.token) headers.Authorization = `Bearer ${target.token}`;
  return {
    method,
    headers,
    credentials: target.baseUrl ? "omit" : "include",
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  };
}

async function errorOf(response: Response, fallback: string): Promise<Error> {
  try {
    const body = await response.json() as { error?: string };
    return new Error(body.error ?? fallback);
  } catch {
    return new Error(fallback);
  }
}

export function urlBase64ToUint8Array(value: string): Uint8Array<ArrayBuffer> {
  const padded = (value + "=".repeat((4 - (value.length % 4)) % 4)).replace(/-/g, "+").replace(/_/g, "/");
  const raw = atob(padded);
  const bytes = new Uint8Array(new ArrayBuffer(raw.length));
  for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
  return bytes;
}

export async function fetchVapidKey(target: PushTarget): Promise<string> {
  const response = await fetch(`${target.baseUrl}/api/push/vapid`, init(target, "GET"));
  if (!response.ok) throw await errorOf(response, "This Fleet can't send notifications yet. Update it and try again.");
  return ((await response.json()) as { publicKey: string }).publicKey;
}

/** Saves the subscription with these choices. `previousEndpoint` carries over a rotated subscription's choices. */
export async function saveSubscription(
  target: PushTarget,
  subscription: PushSubscriptionJSON,
  choices: Partial<PushChoices>,
  previousEndpoint?: string | null,
): Promise<SavedSubscription> {
  const response = await fetch(`${target.baseUrl}/api/push/subscriptions`, init(target, "PUT", {
    endpoint: subscription.endpoint,
    keys: subscription.keys,
    kinds: choices.kinds,
    quietWhenDesk: choices.quietWhenDesk,
    previousEndpoint: previousEndpoint ?? null,
  }));
  if (!response.ok) throw await errorOf(response, "Couldn't save the notification settings.");
  return await response.json() as SavedSubscription;
}

/** What home has for this endpoint; null when it doesn't know it (never saved, or removed). */
export async function lookupSubscription(target: PushTarget, endpoint: string): Promise<SavedSubscription | null> {
  const response = await fetch(`${target.baseUrl}/api/push/subscriptions/lookup`, init(target, "POST", { endpoint }));
  if (response.status === 404) return null;
  if (!response.ok) throw await errorOf(response, "Couldn't read the notification settings.");
  return await response.json() as SavedSubscription;
}

export async function deleteSubscription(target: PushTarget, endpoint: string): Promise<void> {
  const response = await fetch(`${target.baseUrl}/api/push/subscriptions`, init(target, "DELETE", { endpoint }));
  if (!response.ok && response.status !== 404) throw await errorOf(response, "Couldn't turn notifications off.");
}

/** Asks home to push a test to this endpoint. Returns `delivered`, `gone`, `retry_later` or `failed`. */
export async function sendTestPush(target: PushTarget, endpoint: string): Promise<string> {
  const response = await fetch(`${target.baseUrl}/api/push/test`, init(target, "POST", { endpoint }));
  if (!response.ok) throw await errorOf(response, "Couldn't send a test notification.");
  return ((await response.json()) as { outcome: string }).outcome;
}

/** Subscribes this browser to push with home's VAPID key. Call it from a click (iOS insists). */
export async function subscribeBrowser(registration: ServiceWorkerRegistration, publicKey: string): Promise<PushSubscription> {
  return await registration.pushManager.subscribe({
    userVisibleOnly: true,
    applicationServerKey: urlBase64ToUint8Array(publicKey),
  });
}

/** The phone's on/off switches, grouped as the setup screen shows them. "Needs you" covers workflow waits too. */
export const CHOICE_GROUPS: { id: string; label: string; detail: string; kinds: PushKind[] }[] = [
  { id: "needs-you", label: "Needs you", detail: "A command or edit waits for approval", kinds: ["permission", "workflow"] },
  { id: "questions", label: "Questions", detail: "An agent asked you something", kinds: ["question"] },
  { id: "finished", label: "Finished", detail: "A session's turn ended", kinds: ["finished"] },
  { id: "failed", label: "Failed", detail: "A session stopped with an error", kinds: ["failed"] },
];

/** The kinds the switched-on groups stand for. */
export function kindsFor(groupIds: readonly string[]): PushKind[] {
  return CHOICE_GROUPS.filter((group) => groupIds.includes(group.id)).flatMap((group) => group.kinds);
}

/** The groups whose kinds are all in `kinds`. */
export function groupsFor(kinds: readonly string[]): string[] {
  return CHOICE_GROUPS.filter((group) => group.kinds.every((kind) => kinds.includes(kind))).map((group) => group.id);
}
