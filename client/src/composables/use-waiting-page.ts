import { computed, toValue, type MaybeRefOrGetter } from "vue";
import { useCanvasesStore } from "@/stores/canvases";
import { useSessionsStore } from "@/stores/sessions";

/**
 * The page canvas the agent waits on for a pick, while the session is stopped on you; null otherwise. The desktop card
 * and the phone's docked bar share it, so they can't disagree about when the session is waiting on a page.
 */
export function useWaitingPage(sessionId: MaybeRefOrGetter<string>) {
  const canvasesStore = useCanvasesStore();
  const sessionsStore = useSessionsStore();
  return computed(() => {
    const id = toValue(sessionId);
    if (sessionsStore.sessionById(id)?.activityStatus !== "waiting_input") return null;
    return canvasesStore.sessionCanvases(id).canvases.find((canvas) => canvas.page?.choice?.status === "waiting") ?? null;
  });
}
