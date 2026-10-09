// One Live Activity per working session, kept in step with the hub while the app runs. iOS only lets an app start
// one while it's in the foreground (from the background it takes a push-to-start token), so it starts as soon as a
// session works. expo-widgets' start() is a synchronous native call (Activity.request); it's timed in the log.
// Updating one while the app is suspended needs Fleet to push to the activity's APNs token (see the findings).
import type { LiveActivity } from "expo-widgets";
import SessionActivity, { type SessionActivityProps } from "~/widgets/SessionActivity";
import type { ActivityState } from "~/fleet/live-activity";

const running = new Map<string, LiveActivity<SessionActivityProps>>();

export type { ActivityState };

export async function syncLiveActivity({ sessionId, ...props }: ActivityState): Promise<void> {
  const existing = running.get(sessionId);
  if (existing) {
    await existing.update(props).catch((error: unknown) => console.warn("[live-activity] update failed:", error));
    return;
  }
  const began = Date.now();
  try {
    running.set(sessionId, SessionActivity.start(props, `fleet://s/${sessionId}`));
    console.log(`[live-activity] start took ${Date.now() - began} ms`);
  } catch (error) {
    console.warn(`[live-activity] start failed after ${Date.now() - began} ms:`, error);
  }
}

export async function endLiveActivity(sessionId: string): Promise<void> {
  const activity = running.get(sessionId);
  running.delete(sessionId);
  await activity?.end("default").catch(() => {});
}
