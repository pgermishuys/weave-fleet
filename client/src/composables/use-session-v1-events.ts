import { onMounted, onUnmounted } from "vue";
import { useWeaveSocket, type TopicCallback } from "@/composables/use-weave-socket";

type Unsubscribe = () => void;

const SESSIONS_V1_LIFECYCLE_EVENTS = new Set([
  "session_created",
  "session_stopped",
  "session_deleted",
  "session_archived",
  "session_unarchived",
]);

/**
 * Subscribes to the `sessions-v1` WebSocket topic and triggers a refetch callback
 * when any V1 session lifecycle event arrives.
 *
 * Used by V1 dashboard/panel components to stay in sync without polling aggressively.
 */
export function useSessionV1Events(onLifecycleEvent: () => void): void {
  const { subscribe } = useWeaveSocket();
  let unsubscribe: Unsubscribe | null = null;

  onMounted(() => {
    const topicCallback: TopicCallback = (_topic: string, data: unknown) => {
      const message = data as { type?: string } | null;
      if (!message?.type) {
        return;
      }

      if (SESSIONS_V1_LIFECYCLE_EVENTS.has(message.type)) {
        onLifecycleEvent();
      }
    };

    unsubscribe = subscribe(["sessions-v1"], topicCallback);
  });

  onUnmounted(() => {
    unsubscribe?.();
    unsubscribe = null;
  });
}
