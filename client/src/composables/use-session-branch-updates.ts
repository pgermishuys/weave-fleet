import { onMounted, onUnmounted } from "vue";
import { onDomainEvent } from "@/composables/on-domain-event";
import { liveTarget } from "@/lib/machine-target";
import { useSessionsStore } from "@/stores/sessions";

/**
 * Applies a pushed branch change to every session in the workspace (an agent ran `git switch` in its
 * folder), so the row, the header and the pull request link follow without a reload.
 */
export function useSessionBranchUpdates(): void {
  const sessionsStore = useSessionsStore();
  let unsubscribe: (() => void) | null = null;

  onMounted(() => {
    unsubscribe = onDomainEvent(liveTarget(), "sessions", "session_branch", (event) => {
      const { workspaceId, branch } = event.payload;
      for (const item of sessionsStore.sessions) {
        if (item.workspaceId === workspaceId) sessionsStore.patchSession(item.session.id, { branch });
      }
    });
  });

  onUnmounted(() => {
    unsubscribe?.();
  });
}
