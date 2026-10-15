import { onMounted, onUnmounted } from "vue";
import { onDomainEvent } from "@/composables/on-domain-event";
import { liveTarget } from "@/lib/machine-target";
import { useSessionsStore } from "@/stores/sessions";

/**
 * Applies pushed checkout changes to every session in the workspace (a workspace can hold a session and
 * its subagent sessions), so the header can say the checkout was removed and offer to put it back.
 */
export function useSessionCheckoutUpdates(): void {
  const sessionsStore = useSessionsStore();
  let unsubscribe: (() => void) | null = null;

  onMounted(() => {
    unsubscribe = onDomainEvent(liveTarget(), "sessions", "session_checkout", (event) => {
      const { workspaceId, state, removedAt } = event.payload;
      const patch = state === "removed"
        ? { checkoutRemovedAt: removedAt, checkoutRestoring: false }
        : state === "restoring"
          ? { checkoutRestoring: true }
          : { checkoutRemovedAt: null, checkoutRestoring: false };

      for (const item of sessionsStore.sessions) {
        if (item.workspaceId === workspaceId) sessionsStore.patchSession(item.session.id, patch);
      }
    });
  });

  onUnmounted(() => {
    unsubscribe?.();
  });
}
