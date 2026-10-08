// Keeps a Live Activity (iOS) per session that is working or waiting on you, from the hub's "sessions" topic.
import type { ActivityStatusPayload, DomainEvent } from "@fleet/lib/domain-events";
import { toneOf } from "~/components/ui";
import { fleet } from "~/fleet/api";
import { currentCredentials } from "~/fleet/credentials";
import { endLiveActivity, syncLiveActivity } from "~/fleet/live-activity";

const titles = new Map<string, string>();
const started = new Map<string, number>();

async function titleOf(sessionId: string): Promise<string> {
  if (!titles.has(sessionId)) {
    for (const item of await fleet.sessions().catch(() => [])) titles.set(item.session.id, item.session.title);
  }
  return titles.get(sessionId) ?? "Session";
}

export function trackActivity(event: DomainEvent): void {
  if (event.type !== "activity_status") return;
  const { sessionId, activityStatus } = event.payload as ActivityStatusPayload;
  const tone = toneOf(activityStatus);
  if (tone !== "working" && tone !== "needs-you") {
    started.delete(sessionId);
    void endLiveActivity(sessionId);
    return;
  }
  if (!started.has(sessionId)) started.set(sessionId, Date.now());
  void titleOf(sessionId).then((title) =>
    syncLiveActivity({
      sessionId,
      title,
      state: tone === "needs-you" ? "Needs you" : "Working",
      detail: tone === "needs-you" ? "The agent is waiting for you" : `on ${currentCredentials()?.machineName ?? "Fleet"}`,
      startedAt: started.get(sessionId)!,
      needsYou: tone === "needs-you",
    }),
  );
}
