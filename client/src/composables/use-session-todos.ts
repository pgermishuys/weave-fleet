import { computed, type ComputedRef, type MaybeRefOrGetter } from "vue";
import { useSessionEventsSwitch } from "@/composables/use-session-events-switch";
import { extractLatestTodos, type TodoItem } from "@/lib/todo-utils";

export function useSessionTodos(
  sessionId: MaybeRefOrGetter<string>,
  instanceId: MaybeRefOrGetter<string>,
): { todos: ComputedRef<readonly TodoItem[]> } {
  const { messages } = useSessionEventsSwitch(sessionId, instanceId);

  const todos = computed<readonly TodoItem[]>(() => extractLatestTodos(messages.value));

  return {
    todos,
  };
}
