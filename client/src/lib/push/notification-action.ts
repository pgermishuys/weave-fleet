/**
 * Answering a permission ask from a notification button, as the service worker does it: find the phone's key for the
 * machine that asked, post the same answer the desktop card sends, and say what happened. No Vue and no `window`.
 */
import { credentialFor, type DeviceCredentials } from "@/lib/device-credentials";
import { permissionAnswerRequest, sendAnswer, type AnswerOutcome, type PermissionReply } from "@/lib/push/answer";
import { ALLOW_ONCE_ACTION, DENY_ACTION, type NotificationData } from "@/lib/push/notification-options";

export type NotificationActionResult =
  | { kind: "answered"; allowed: boolean }
  /** Couldn't answer from here (no key, already answered, offline): open the ask instead. */
  | { kind: "open"; url: string; reason: string };

export function replyForAction(action: string): PermissionReply | null {
  if (action === ALLOW_ONCE_ACTION) return "once";
  if (action === DENY_ACTION) return "reject";
  return null;
}

/**
 * Answers the ask behind `data` with `action`. Home is this origin (the cookie or the phone's token); any other
 * machine is reached with the phone's own token for it, never with cookies. The token never goes in the notification.
 */
export async function answerFromNotification(
  action: string,
  data: NotificationData,
  credentials: DeviceCredentials | null,
  origin: string,
  fetcher: typeof fetch,
): Promise<NotificationActionResult> {
  const reply = replyForAction(action);
  if (!reply || !data.requestId) return { kind: "open", url: data.url, reason: "no-ask" };

  let target: { baseUrl: string; token: string | null };
  if (!credentials || data.machineId === credentials.homeMachineId) {
    target = { baseUrl: "", token: credentials?.token ?? null };
  } else {
    const key = credentialFor(credentials, data.machineId);
    if (!key) return { kind: "open", url: data.url, reason: "no-key" };
    target = { baseUrl: key.baseUrl === origin ? "" : key.baseUrl, token: key.token };
  }

  const outcome: AnswerOutcome = await sendAnswer(permissionAnswerRequest(target, data.sessionId, data.requestId, reply), fetcher);
  if (outcome.ok) return { kind: "answered", allowed: reply === "once" };
  return { kind: "open", url: data.url, reason: outcome.gone ? "gone" : "failed" };
}
