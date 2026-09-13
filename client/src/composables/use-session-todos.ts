import type { ComputedRef, MaybeRefOrGetter } from "vue";
import { useSessionProgress } from "@/composables/use-session-progress";
import type { TodoItem } from "@/lib/todo-utils";

/** The agent's todo list for a session, as the server last reported it. */
export function useSessionTodos(
  sessionId: MaybeRefOrGetter<string>,
): { todos: ComputedRef<readonly TodoItem[]> } {
  const { todos } = useSessionProgress(sessionId);

  return {
    todos,
  };
}
