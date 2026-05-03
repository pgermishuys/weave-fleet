<script setup lang="ts">
import { computed } from "vue";
import { Badge } from "@/components/ui/badge";
import { Progress } from "@/components/ui/progress";
import type { TodoItem as TodoEntry } from "@/lib/todo-utils";

const props = withDefaults(defineProps<{
  todos: readonly TodoEntry[];
  emptyMessage?: string;
  ariaLabel?: string;
}>(), {
  emptyMessage: "No todos yet.",
  ariaLabel: "Todo list",
});

const completedCount = computed(() => props.todos.filter((item) => item.status === "completed").length);
const percentComplete = computed(() => {
  if (props.todos.length === 0) {
    return 0;
  }

  return Math.round((completedCount.value / props.todos.length) * 100);
});
const todoSummary = computed(() => `${completedCount.value} of ${props.todos.length} complete`);

function getPriorityBadgeClass(priority: TodoEntry["priority"]): string {
  switch (priority) {
    case "high":
      return "todo-priority-badge todo-priority-badge--high";
    case "medium":
      return "todo-priority-badge todo-priority-badge--medium";
    default:
      return "todo-priority-badge todo-priority-badge--low";
  }
}

function getStatusLabel(status: TodoEntry["status"]): string {
  switch (status) {
    case "in_progress":
      return "In progress";
    case "completed":
      return "Completed";
    case "cancelled":
      return "Cancelled";
    default:
      return "Pending";
  }
}

function getStatusIndicatorClass(status: TodoEntry["status"]): string {
  return `todo-list-view__status-indicator todo-list-view__status-indicator--${status}`;
}
</script>

<template>
  <section
    class="todo-list-view"
    :aria-label="ariaLabel"
  >
    <div
      v-if="todos.length > 0"
      class="todo-list-view__summary-row"
    >
      <p class="todo-list-view__summary">
        {{ todoSummary }}
      </p>
      <p class="todo-list-view__count">
        {{ percentComplete }}%
      </p>
    </div>

    <Progress
      v-if="todos.length > 0"
      :model-value="percentComplete"
      class="todo-list-view__progress"
    />

    <p
      v-if="todos.length === 0"
      class="todo-list-view__empty"
    >
      {{ emptyMessage }}
    </p>

    <ul
      v-else
      class="todo-list-view__list"
    >
      <li
        v-for="item in todos"
        :key="`${item.content}-${item.status}-${item.priority}`"
        class="todo-list-view__item"
      >
        <span
          :class="getStatusIndicatorClass(item.status)"
          :aria-label="getStatusLabel(item.status)"
          :title="getStatusLabel(item.status)"
        >
          <span class="todo-list-view__status-indicator-core" />
        </span>

        <span
          class="todo-list-view__content"
          :class="{
            'todo-list-view__content--muted': item.status === 'completed' || item.status === 'cancelled',
          }"
        >
          {{ item.content }}
        </span>

        <Badge
          variant="outline"
          :class="getPriorityBadgeClass(item.priority)"
        >
          {{ item.priority }}
        </Badge>
      </li>
    </ul>
  </section>
</template>

<style scoped>
.todo-list-view {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.todo-list-view__summary-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.todo-list-view__summary,
.todo-list-view__count,
.todo-list-view__empty {
  margin: 0;
  font-size: 10px;
  color: var(--muted);
}

.todo-list-view__progress {
  height: 6px;
}

.todo-list-view__list {
  margin: 0;
  padding: 0;
  list-style: none;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.todo-list-view__item {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  font-size: 11px;
  line-height: 1.4;
}

.todo-list-view__status-indicator {
  position: relative;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 14px;
  height: 14px;
  flex-shrink: 0;
  margin-top: 1px;
  border-radius: 999px;
  border: 1px solid color-mix(in srgb, var(--border) 88%, transparent);
  background: color-mix(in srgb, var(--muted) 12%, transparent);
}

.todo-list-view__status-indicator-core {
  width: 6px;
  height: 6px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--muted) 72%, transparent);
}

.todo-list-view__status-indicator--pending {
  border-color: color-mix(in srgb, var(--border) 92%, transparent);
  background: transparent;
}

.todo-list-view__status-indicator--pending .todo-list-view__status-indicator-core {
  width: 4px;
  height: 4px;
  background: color-mix(in srgb, var(--muted) 78%, transparent);
}

.todo-list-view__status-indicator--in_progress {
  border-color: color-mix(in srgb, var(--primary, #6366f1) 30%, var(--border));
  background: color-mix(in srgb, var(--primary, #6366f1) 10%, transparent);
}

.todo-list-view__status-indicator--in_progress .todo-list-view__status-indicator-core {
  background: color-mix(in srgb, var(--primary, #6366f1) 72%, white 28%);
}

.todo-list-view__status-indicator--completed {
  border-color: color-mix(in srgb, #22c55e 28%, var(--border));
  background: color-mix(in srgb, #22c55e 10%, transparent);
}

.todo-list-view__status-indicator--completed .todo-list-view__status-indicator-core {
  background: #22c55e;
}

.todo-list-view__status-indicator--cancelled {
  border-color: color-mix(in srgb, #ef4444 20%, var(--border));
  background: color-mix(in srgb, #ef4444 8%, transparent);
}

.todo-list-view__status-indicator--cancelled .todo-list-view__status-indicator-core {
  background: color-mix(in srgb, #ef4444 62%, white 38%);
}

.todo-list-view__content {
  flex: 1;
  min-width: 0;
  color: var(--text);
  overflow-wrap: anywhere;
}

.todo-list-view__content--muted {
  color: var(--muted);
  text-decoration: line-through;
}

.todo-priority-badge {
  margin-top: 1px;
  padding: 0 4px;
  font-size: 10px;
  line-height: 1.4;
}

.todo-priority-badge--high {
  border-color: rgb(248 113 113 / 0.3);
  color: rgb(220 38 38);
}

.todo-priority-badge--medium {
  border-color: rgb(251 191 36 / 0.3);
  color: rgb(217 119 6);
}

.todo-priority-badge--low {
  border-color: var(--border);
  color: var(--muted);
}
</style>
