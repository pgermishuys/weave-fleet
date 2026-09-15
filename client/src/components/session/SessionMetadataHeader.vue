<script setup lang="ts">
import { computed } from "vue";
import ProgressRing from "@/components/sessions/ProgressRing.vue";
import { useSessionProgress } from "@/composables/use-session-progress";
import { currentPlanStep, plainText } from "@/lib/session-progress";
import { useCanvasesStore } from "@/stores/canvases";

/**
 * A one-line summary of the session's progress along the foot of the right panel: the plan's current step, or the todo
 * being worked on. Hidden while the Progress tab is open, which shows it all; clicking it opens that tab.
 * Linked pull requests and issues live in the Context tab.
 */
const props = defineProps<{
  sessionId: string;
}>();

const canvasesStore = useCanvasesStore();
const { progress } = useSessionProgress(computed(() => props.sessionId));

const isProgressTabActive = computed(() => canvasesStore.sessionCanvases(props.sessionId).activeId === "progress");
const visible = computed(() => (progress.value?.total ?? 0) > 0 && !isProgressTabActive.value);

const heading = computed(() => {
  const plan = progress.value?.plan;
  if (!plan) return "Todos";
  const current = currentPlanStep(plan);
  return plainText(current?.group.title ?? plan.title ?? plan.path);
});

const currentLine = computed(() => {
  const value = progress.value;
  if (!value) return "";
  const plan = value.plan;
  if (plan) {
    const current = currentPlanStep(plan);
    if (!current) return "Every step is ticked";
    const number = current.step.number ? `${current.step.number}. ` : "";
    return `Next: ${number}${plainText(current.step.title)}`;
  }
  return value.current ?? "Every todo is done";
});

const label = computed(() => {
  const value = progress.value;
  if (!value) return "";
  const unit = value.plan ? "steps" : "todos";
  return `${value.done} of ${value.total} ${unit} done. ${currentLine.value}. Open the Progress tab`;
});

const percent = computed(() => {
  const value = progress.value;
  return value && value.total > 0 ? Math.min(100, (value.done / value.total) * 100) : 0;
});

function openProgress(): void {
  canvasesStore.open(props.sessionId, "progress");
}
</script>

<template>
  <button
    v-if="visible && progress"
    type="button"
    class="progress-strip"
    :aria-label="label"
    :title="label"
    @click="openProgress"
  >
    <span
      class="progress-strip__bar"
      :style="{ width: `${percent}%` }"
      aria-hidden="true"
    />
    <ProgressRing
      :done="progress.done"
      :total="progress.total"
      :size="14"
      :stroke-width="2"
    />
    <span class="progress-strip__copy">
      <span class="progress-strip__heading">{{ heading }}</span>
      <span class="progress-strip__current">{{ currentLine }}</span>
    </span>
    <span class="progress-strip__count">{{ progress.done }}/{{ progress.total }}</span>
  </button>
</template>

<style scoped>
/* A status line along the foot of the panel, part of its chrome rather than a card on the canvas. */
.progress-strip {
  position: relative;
  display: flex;
  flex-shrink: 0;
  align-items: center;
  gap: 8px;
  width: 100%;
  height: 36px;
  padding: 0 12px;
  border: 0;
  border-top: 1px solid var(--border);
  background: transparent;
  color: var(--muted);
  font-size: 12px;
  text-align: left;
  cursor: pointer;
  transition: background-color var(--transition), color var(--transition);
}

.progress-strip:hover {
  background: color-mix(in srgb, var(--text) 4%, transparent);
  color: var(--text);
}

.progress-strip:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

/* How much is done, drawn along the top border. */
.progress-strip__bar {
  position: absolute;
  top: -1px;
  left: 0;
  height: 1px;
  background: var(--accent);
  transition: width 300ms ease-out;
}

.progress-strip__copy {
  flex: 1;
  min-width: 0;
  display: flex;
  align-items: baseline;
  gap: 6px;
  overflow: hidden;
  white-space: nowrap;
}

.progress-strip__heading {
  flex-shrink: 1;
  min-width: 0;
  max-width: 45%;
  overflow: hidden;
  text-overflow: ellipsis;
}

.progress-strip__heading::after {
  content: "·";
  margin-left: 6px;
}

.progress-strip__current {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  color: var(--text);
}

.progress-strip__count {
  flex-shrink: 0;
  font-variant-numeric: tabular-nums;
}

@media (prefers-reduced-motion: reduce) {
  .progress-strip,
  .progress-strip__bar {
    transition: none;
  }
}
</style>
