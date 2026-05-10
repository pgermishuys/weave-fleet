<script setup lang="ts">
import { computed } from "vue";
import { ChevronLeft, ListTodo } from "lucide-vue-next";
import type { TodoItem } from "@/lib/todo-utils";

const props = defineProps<{
  todos: readonly TodoItem[];
}>();

const emit = defineEmits<{
  expand: [];
}>();

const totalCount = computed(() => props.todos.length);
const completedCount = computed(() => props.todos.filter((todo) => todo.status === "completed").length);
const pendingCount = computed(() => props.todos.filter((todo) => todo.status !== "completed" && todo.status !== "cancelled").length);
const progressOffset = computed(() => {
  if (totalCount.value === 0) {
    return 75.4;
  }

  const completionRatio = completedCount.value / totalCount.value;
  return 75.4 - (75.4 * completionRatio);
});

function handleExpand(): void {
  emit("expand");
}
</script>

<template>
  <aside
    class="collapsed-rail"
    aria-label="Expand right panel"
  >
    <button
      type="button"
      class="collapsed-rail__button"
      @click="handleExpand"
    >
      <span class="collapsed-rail__top">
        <ChevronLeft
          class="collapsed-rail__chevron"
          :size="16"
          aria-hidden="true"
        />
      </span>

      <span class="collapsed-rail__body">
        <span
          class="collapsed-rail__progress"
          aria-hidden="true"
        >
          <svg
            viewBox="0 0 32 32"
            class="collapsed-rail__progress-chart"
          >
            <circle
              class="collapsed-rail__progress-track"
              cx="16"
              cy="16"
              r="12"
            />
            <circle
              class="collapsed-rail__progress-value"
              cx="16"
              cy="16"
              r="12"
              :stroke-dashoffset="progressOffset"
            />
          </svg>
          <span class="collapsed-rail__progress-count">{{ totalCount }}</span>
        </span>

        <span class="collapsed-rail__label">
          <ListTodo
            :size="14"
            aria-hidden="true"
          />
          <span>{{ pendingCount }}</span>
        </span>
      </span>
    </button>
  </aside>
</template>

<style scoped>
.collapsed-rail {
  width: 48px;
  min-width: 48px;
  min-height: 0;
  border-left: 1px solid var(--border);
  background: var(--panel-bg);
  display: flex;
}

.collapsed-rail__button {
  width: 100%;
  min-height: 0;
  border: 0;
  background: transparent;
  color: var(--text);
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 18px;
  padding: 12px 8px;
  cursor: pointer;
  transition: background-color 0.15s ease, color 0.15s ease;
}

.collapsed-rail__button:hover {
  background: rgba(255, 255, 255, 0.04);
}

.collapsed-rail__button:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.collapsed-rail__top,
.collapsed-rail__body {
  display: flex;
  flex-direction: column;
  align-items: center;
}

.collapsed-rail__body {
  gap: 12px;
  margin-top: 4px;
}

.collapsed-rail__chevron {
  color: var(--muted);
}

.collapsed-rail__progress {
  position: relative;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 32px;
  height: 32px;
}

.collapsed-rail__progress-chart {
  width: 32px;
  height: 32px;
  transform: rotate(-90deg);
}

.collapsed-rail__progress-track,
.collapsed-rail__progress-value {
  fill: none;
  stroke-width: 3;
}

.collapsed-rail__progress-track {
  stroke: color-mix(in srgb, var(--border) 75%, transparent);
}

.collapsed-rail__progress-value {
  stroke: var(--accent);
  stroke-linecap: round;
  stroke-dasharray: 75.4;
  transition: stroke-dashoffset 0.2s ease;
}

.collapsed-rail__progress-count {
  position: absolute;
  font-size: 10px;
  font-weight: 700;
  line-height: 1;
}

.collapsed-rail__label {
  display: inline-flex;
  flex-direction: column;
  align-items: center;
  gap: 2px;
  font-size: 10px;
  font-weight: 700;
  color: var(--muted);
}
</style>
