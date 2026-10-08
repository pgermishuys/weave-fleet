import { onMounted, onUnmounted } from "vue";
import { onGlobalEvent } from "@/composables/use-signalr-socket";
import { liveTarget } from "@/lib/machine-target";
import type { DomainEvent } from "@/lib/domain-events";
import { useSessionsStore } from "@/stores/sessions";

/** The server's push after a turn's tokens are counted (AnalyticsWriterService.SessionTokensEventType). */
export const SESSION_TOKENS = "session_tokens";

interface SessionTokens {
  sessionId: string;
  totalTokens: number;
  totalCost: number;
}

function parseSessionTokens(payload: unknown): SessionTokens | null {
  if (typeof payload !== "object" || payload === null) return null;
  const { sessionId, totalTokens, totalCost } = payload as Record<string, unknown>;
  return typeof sessionId === "string" && typeof totalTokens === "number" && typeof totalCost === "number"
    ? { sessionId, totalTokens, totalCost }
    : null;
}

/**
 * Applies a session's new token and cost totals as soon as they are counted, so the status bar and its row don't
 * say "0 tokens" until the session list's next poll.
 */
export function useSessionTokenUpdates(): void {
  const sessionsStore = useSessionsStore();
  let unsubscribe: (() => void) | null = null;

  onMounted(() => {
    unsubscribe = onGlobalEvent(liveTarget(), "sessions", (event: DomainEvent) => {
      if ((event.type as string) !== SESSION_TOKENS) return;
      const totals = parseSessionTokens(event.payload);
      if (!totals) return;
      sessionsStore.patchSession(totals.sessionId, { totalTokens: totals.totalTokens, totalCost: totals.totalCost });
    });
  });

  onUnmounted(() => {
    unsubscribe?.();
  });
}
