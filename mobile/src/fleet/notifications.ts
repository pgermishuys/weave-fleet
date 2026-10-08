// Native notifications with actions: Allow once / Deny on a permission ask, the choices plus a typed answer on a
// question. Answered from the notification itself, without opening the app. While the app runs they come from the
// hub's "sessions" topic; when it doesn't, Fleet would have to push them through APNs/FCM (see the findings).
import * as Notifications from "expo-notifications";
import { router } from "expo-router";
import { Platform } from "react-native";
import { pendingQuestion } from "@fleet/lib/phone/dock-state";
import { createSessionStreamState } from "@fleet/lib/domain-event-reducer";
import type { SessionNotificationPayload } from "@fleet/lib/domain-events";
import type { SessionSnapshot } from "@fleet/lib/session-snapshot";
import { fleet } from "~/fleet/api";
import { hub } from "~/fleet/hub";

const supported = Platform.OS !== "web";

export async function setupNotifications(): Promise<boolean> {
  if (!supported) return false;
  Notifications.setNotificationHandler({
    handleNotification: async () => ({ shouldShowBanner: true, shouldShowList: true, shouldPlaySound: true, shouldSetBadge: false }),
  });
  if (Platform.OS === "android") {
    await Notifications.setNotificationChannelAsync("asks", {
      name: "Needs you",
      importance: Notifications.AndroidImportance.HIGH,
      vibrationPattern: [0, 120, 80, 120],
    });
  }
  await Notifications.setNotificationCategoryAsync("permission", [
    { identifier: "allow", buttonTitle: "Allow once", options: { opensAppToForeground: false } },
    { identifier: "deny", buttonTitle: "Deny", options: { opensAppToForeground: false, isDestructive: true } },
  ]);
  const { status } = await Notifications.requestPermissionsAsync();
  return status === "granted";
}

/** Questions get their own category per ask: its first two choices as buttons, then "Answer…" with a text box. */
async function questionCategory(options: string[]): Promise<string> {
  const id = `question-${options.slice(0, 2).join("|")}`.slice(0, 60);
  await Notifications.setNotificationCategoryAsync(id, [
    ...options.slice(0, 2).map((label, index) => ({ identifier: `opt:${index}`, buttonTitle: label, options: { opensAppToForeground: false } })),
    { identifier: "reply", buttonTitle: "Answer…", textInput: { submitButtonTitle: "Send", placeholder: "Your answer" }, options: { opensAppToForeground: false } },
  ]);
  return id;
}

export async function notifyFromFleet(payload: SessionNotificationPayload): Promise<void> {
  if (!supported) return;
  const data: Record<string, unknown> = { sessionId: payload.sessionId, kind: payload.kind ?? payload.reason };
  let categoryIdentifier: string | undefined;
  let body = payload.body;

  if (payload.kind === "permission" && payload.requestId) {
    categoryIdentifier = "permission";
    data.requestId = payload.requestId;
  } else if (payload.kind === "question") {
    // The payload carries no question; read it from the session, as the web app's dock does.
    const snapshot = await hub.peek(payload.sessionId).catch(() => null);
    const pending = snapshot ? pendingQuestion(createSessionStreamState(snapshot).messages) : null;
    if (pending) {
      const labels = pending.question.options.map((option) => option.label);
      categoryIdentifier = await questionCategory(labels);
      data.requestId = pending.requestId;
      data.options = labels;
      body = pending.question.question;
    }
  }

  await Notifications.scheduleNotificationAsync({
    identifier: `${payload.sessionId}:${payload.kind ?? payload.reason}`,
    content: { title: payload.title, body, data, categoryIdentifier, ...(Platform.OS === "android" ? { channelId: "asks" } : {}) },
    trigger: Platform.OS === "android" ? { channelId: "asks" } : null,
  });
}

async function handleResponse(response: Notifications.NotificationResponse): Promise<void> {
  const data = response.notification.request.content.data as { sessionId?: string; requestId?: string; options?: string[] };
  const action = response.actionIdentifier;
  if (!data.sessionId) return;
  try {
    if (action === "allow" && data.requestId) await fleet.replyPermission(data.sessionId, data.requestId, "once");
    else if (action === "deny" && data.requestId) await fleet.replyPermission(data.sessionId, data.requestId, "reject");
    else if (action.startsWith("opt:") && data.requestId && data.options) await fleet.answerQuestion(data.sessionId, data.requestId, [[data.options[Number(action.slice(4))]]]);
    else if (action === "reply" && data.requestId && response.userText?.trim()) await fleet.answerQuestion(data.sessionId, data.requestId, [[response.userText.trim()]]);
    else {
      router.push(`/s/${data.sessionId}`);
      return;
    }
    await Notifications.dismissNotificationAsync(response.notification.request.identifier);
  } catch (error) {
    await Notifications.scheduleNotificationAsync({
      content: { title: "Couldn't answer", body: error instanceof Error ? error.message : String(error), data: { sessionId: data.sessionId } },
      trigger: null,
    });
  }
}

export function listenForResponses(): () => void {
  if (!supported) return () => {};
  const subscription = Notifications.addNotificationResponseReceivedListener((response) => void handleResponse(response));
  // A tap that launched the app.
  const last = Notifications.getLastNotificationResponse();
  if (last) void handleResponse(last);
  return () => subscription.remove();
}
