<script setup lang="ts">
import { computed } from "vue";
import type { TodoItem as TodoEntry } from "@/lib/todo-utils";

const props = defineProps<{
  item: TodoEntry;
}>();

const checkClasses = computed<Record<string, boolean>>(() => ({
  "todo-check": true,
  checked: props.item.status === "completed",
  "in-progress": props.item.status === "in_progress",
}));

const textClasses = computed<Record<string, boolean>>(() => ({
  "todo-text": true,
  done: props.item.status === "completed",
}));

const statusLabel = computed(() => {
  switch (props.item.status) {
    case "completed":
      return "Completed";
    case "in_progress":
      return "In progress";
    case "cancelled":
      return "Cancelled";
    case "pending":
    default:
      return "Pending";
  }
});

const indicator = computed(() => {
  switch (props.item.status) {
    case "completed":
      return "✓";
    case "in_progress":
      return "•";
    default:
      return "";
  }
});
</script>

<template>
  <li class="todo-item">
    <span :class="checkClasses" aria-hidden="true">
      {{ indicator }}
    </span>

    <div class="todo-content">
      <span :class="textClasses">{{ item.content }}</span>
      <span class="todo-status">{{ statusLabel }}</span>
    </div>
  </li>
</template>

<style scoped>
.todo-item {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  padding: 6px 0;
  font-size: 12px;
  line-height: 1.4;
}

.todo-check {
  width: 16px;
  height: 16px;
  border: 1.5px solid #3f3f46;
  border-radius: 4px;
  flex-shrink: 0;
  margin-top: 1px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  font-size: 11px;
  font-weight: 700;
  color: transparent;
}

.todo-check.checked {
  background: var(--accent);
  border-color: var(--accent);
  color: #fff;
}

.todo-check.in-progress {
  border-color: var(--accent);
  color: var(--accent);
}

.todo-content {
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.todo-text {
  color: var(--text);
}

.todo-text.done {
  text-decoration: line-through;
  color: var(--muted);
}

.todo-status {
  font-size: 11px;
  color: var(--muted);
}
</style>
