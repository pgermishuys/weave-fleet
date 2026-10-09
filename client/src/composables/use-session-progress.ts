import { computed, onScopeDispose, toValue, watch, type ComputedRef, type MaybeRefOrGetter } from "vue";
import { onDomainEvent } from "@/composables/on-domain-event";
import { onReconnect } from "@/composables/use-signalr-socket";
import { PROGRESS_UPDATED, parseProgressDetail, type SessionProgressDetail } from "@/lib/session-progress";
import type { TodoItem } from "@/lib/todo-utils";
import { useSessionProgressStore } from "@/stores/session-progress";
import { useMachineTarget } from "@/lib/machine-target";

/**
 * A session's progress: loaded from the API, then kept current by the server's pushes on the session's
 * topic. Loads again after a reconnect, since pushes may have been missed.
 */
export function useSessionProgress(sessionId: MaybeRefOrGetter<string>): {
  progress: ComputedRef<SessionProgressDetail | null>;
  todos: ComputedRef<readonly TodoItem[]>;
} {
  const machine = useMachineTarget();
  const store = useSessionProgressStore();
  const id = computed(() => toValue(sessionId));
  let unsubscribe: (() => void) | null = null;

  watch(
    id,
    (next) => {
      unsubscribe?.();
      unsubscribe = null;
      if (!next) return;

      void store.ensureLoaded(machine, next);
      unsubscribe = onDomainEvent(machine, `session:${next}`, PROGRESS_UPDATED, (event) => {
        const detail = parseProgressDetail(event.payload);
        if (detail?.sessionId === next) store.apply(detail);
      });
    },
    { immediate: true },
  );

  const offReconnect = onReconnect(machine, () => {
    if (id.value) void store.ensureLoaded(machine, id.value, { force: true });
  });

  onScopeDispose(() => {
    unsubscribe?.();
    offReconnect();
  });

  const progress = computed(() => (id.value ? store.progressFor(id.value) : null));
  const todos = computed<readonly TodoItem[]>(() => progress.value?.todos ?? []);

  return { progress, todos };
}
