<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { ChevronDown, ChevronRight } from "lucide-vue-next";
import TodoListView from "@/components/session/TodoListView.vue";
import { useSessionTodos } from "@/composables/use-session-todos";

/**
 * Session progress shown under the canvas tabs: the agent's todo list.
 * Linked pull requests and issues live in the Context tab.
 */
const props = defineProps<{
  sessionId: string;
}>();

const isTodosExpanded = shallowRef(false);

const { todos } = useSessionTodos(computed(() => props.sessionId));
const completedTodosCount = computed(() => todos.value.filter((t) => t.status === "completed").length);
const todoProgressLabel = computed(() => `${completedTodosCount.value} of ${todos.value.length} todos`);

function toggleTodosExpanded(): void {
  isTodosExpanded.value = !isTodosExpanded.value;
}
</script>

<template>
  <header
    v-if="todos.length > 0"
    class="session-metadata-header"
    aria-label="Session progress"
  >
    <div class="session-meta-chips">
      <button
        type="button"
        class="meta-chip meta-chip--todo"
        :aria-label="`${todoProgressLabel}. ${isTodosExpanded ? 'Collapse' : 'Expand'} todo list`"
        :aria-expanded="isTodosExpanded"
        @click="toggleTodosExpanded"
      >
        <component
          :is="isTodosExpanded ? ChevronDown : ChevronRight"
          :size="11"
          aria-hidden="true"
        />
        {{ todoProgressLabel }}
      </button>
    </div>

    <article
      v-if="isTodosExpanded"
      class="session-section-card"
      role="region"
      aria-label="Session todo list"
    >
      <TodoListView
        :todos="todos"
        aria-label="Session todo list"
      />
    </article>
  </header>
</template>

<style scoped>
.session-metadata-header {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin-top: 8px;
}

.session-section-card {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 7px;
  border: 1px solid var(--border);
  border-radius: 10px;
  background: var(--card-bg);
}

.session-meta-chips {
  display: flex;
  align-items: center;
  gap: 5px;
  flex-wrap: wrap;
}

.meta-chip {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 3px 8px;
  border: 1px solid var(--border);
  border-radius: 10px;
  background: rgba(255, 255, 255, 0.03);
  color: var(--muted);
  font-size: 11px;
  cursor: pointer;
}

.meta-chip:hover {
  background: rgba(255, 255, 255, 0.08);
}

.meta-chip:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}

.meta-chip--todo {
  color: #fbbf24;
  border-color: rgba(245, 158, 11, 0.28);
  background: rgba(245, 158, 11, 0.08);
}
</style>
