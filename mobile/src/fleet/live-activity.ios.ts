// One Live Activity per working session. iOS only shows it outside the app, and expo-widgets' start() is a
// synchronous native call (ActivityKit's Activity.request) made on the JS thread, so a new activity starts when the
// app goes to the background; while it's open, only activities that already exist are updated (asynchronously).
// Updating one while the app is suspended needs Fleet to push to the activity's APNs token (see the findings).
import type { LiveActivity } from "expo-widgets";
import { AppState } from "react-native";
import SessionActivity, { type SessionActivityProps } from "~/widgets/SessionActivity";
import type { ActivityState } from "~/fleet/live-activity";

const running = new Map<string, LiveActivity<SessionActivityProps>>();
const wanted = new Map<string, SessionActivityProps>();

export type { ActivityState };

function start(sessionId: string, props: SessionActivityProps): void {
  const began = Date.now();
  try {
    running.set(sessionId, SessionActivity.start(props, `fleet://s/${sessionId}`));
    console.log(`[live-activity] start took ${Date.now() - began} ms`);
  } catch (error) {
    console.warn(`[live-activity] start failed after ${Date.now() - began} ms:`, error);
  }
}

AppState.addEventListener("change", (state) => {
  if (state !== "background") return;
  for (const [sessionId, props] of wanted) if (!running.has(sessionId)) start(sessionId, props);
});

export async function syncLiveActivity({ sessionId, ...props }: ActivityState): Promise<void> {
  wanted.set(sessionId, props);
  const existing = running.get(sessionId);
  if (existing) {
    await existing.update(props).catch((error: unknown) => console.warn("[live-activity] update failed:", error));
    return;
  }
  if (AppState.currentState === "background") start(sessionId, props);
}

export async function endLiveActivity(sessionId: string): Promise<void> {
  wanted.delete(sessionId);
  const activity = running.get(sessionId);
  running.delete(sessionId);
  await activity?.end("default").catch(() => {});
}
