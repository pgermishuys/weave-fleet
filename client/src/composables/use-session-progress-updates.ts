import { onMounted, onUnmounted } from "vue";
import { onGlobalEvent } from "@/composables/use-signalr-socket";
import type { DomainEvent } from "@/lib/domain-events";
import { SESSION_PROGRESS, parseProgressSummary } from "@/lib/session-progress";
import { useSessionProgressStore } from "@/stores/session-progress";
import { useSessionsStore } from "@/stores/sessions";

/**
 * Applies pushed progress summaries to the session rows, for every session, so rows show how far along
 * each one is without polling.
 */
export function useSessionProgressUpdates(): void {
  const sessionsStore = useSessionsStore();
  const progressStore = useSessionProgressStore();
  let unsubscribe: (() => void) | null = null;

  onMounted(() => {
    unsubscribe = onGlobalEvent("sessions", (event: DomainEvent) => {
      if ((event.type as string) !== SESSION_PROGRESS) return;
      const summary = parseProgressSummary(event.payload);
      if (!summary) return;

      sessionsStore.patchSession(summary.sessionId, { progress: summary });
      progressStore.noteSummary(summary);
    });
  });

  onUnmounted(() => {
    unsubscribe?.();
  });
}
