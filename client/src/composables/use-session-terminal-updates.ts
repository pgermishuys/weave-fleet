import { onMounted, onUnmounted } from "vue";
import { onDomainEvent } from "@/composables/on-domain-event";
import { liveTarget } from "@/lib/machine-target";
import { useSessionsStore } from "@/stores/sessions";

/** Applies a pushed "a terminal has this session open" change to its row, so the chip comes and goes without a reload. */
export function useSessionTerminalUpdates(): void {
  const sessionsStore = useSessionsStore();
  let unsubscribe: (() => void) | null = null;

  onMounted(() => {
    unsubscribe = onDomainEvent(liveTarget(), "sessions", "session_terminal", (event) => {
      const { sessionId, inTerminal } = event.payload;
      sessionsStore.patchSession(sessionId, { inTerminal });
    });
  });

  onUnmounted(() => {
    unsubscribe?.();
  });
}
