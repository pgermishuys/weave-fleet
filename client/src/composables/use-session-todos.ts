import { computed, type ComputedRef, type MaybeRefOrGetter } from "vue";
import { useSessionEvents } from "@/composables/use-session-events";
import { extractLatestTodos, type TodoItem } from "@/lib/todo-utils";

export function useSessionTodos(
  sessionId: MaybeRefOrGetter<string>,
  instanceId: MaybeRefOrGetter<string>,
): { todos: ComputedRef<readonly TodoItem[]> } {
  const { messages } = useSessionEvents(sessionId, instanceId);

  const todos = computed<readonly TodoItem[]>(() => extractLatestTodos(messages.value));

  return {
    todos,
  };
}
