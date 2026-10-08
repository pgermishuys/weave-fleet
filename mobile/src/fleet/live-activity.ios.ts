// One Live Activity per working session, kept in step with the hub while the app runs. Updating it while the app is
// suspended needs Fleet to push to the activity's APNs token (see the findings), which no self-hosted Fleet can do
// without an Apple key.
import type { LiveActivity } from "expo-widgets";
import SessionActivity, { type SessionActivityProps } from "~/widgets/SessionActivity";
import type { ActivityState } from "~/fleet/live-activity";

const running = new Map<string, LiveActivity<SessionActivityProps>>();

export type { ActivityState };

export async function syncLiveActivity({ sessionId, ...props }: ActivityState): Promise<void> {
  try {
    const existing = running.get(sessionId);
    if (existing) {
      await existing.update(props);
      return;
    }
    running.set(sessionId, SessionActivity.start(props, `fleet://s/${sessionId}`));
  } catch (error) {
    console.warn("Live Activity unavailable:", error);
  }
}

export async function endLiveActivity(sessionId: string): Promise<void> {
  const activity = running.get(sessionId);
  running.delete(sessionId);
  await activity?.end("default").catch(() => {});
}
