import { computed, type ComputedRef, type MaybeRefOrGetter } from "vue";
import { useSessionStream } from "@/composables/use-session-stream";
import { extractLatestTodos, type TodoItem } from "@/lib/todo-utils";

export function useSessionTodos(
  sessionId: MaybeRefOrGetter<string>,
): { todos: ComputedRef<readonly TodoItem[]> } {
  const { messages } = useSessionStream(sessionId);

  const todos = computed<readonly TodoItem[]>(() => extractLatestTodos(messages.value));

  return {
    todos,
  };
}
