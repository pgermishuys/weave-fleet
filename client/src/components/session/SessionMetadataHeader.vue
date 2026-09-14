<script setup lang="ts">
import { computed } from "vue";
import ProgressRing from "@/components/sessions/ProgressRing.vue";
import { useSessionProgress } from "@/composables/use-session-progress";
import { currentPlanStep, plainText } from "@/lib/session-progress";
import { useCanvasesStore } from "@/stores/canvases";

/**
 * A one-line summary of the session's progress under the canvas tabs: the plan's current step, or the todo
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
    <ProgressRing
      :done="progress.done"
      :total="progress.total"
      :size="18"
      :stroke-width="2.5"
    />
    <span class="progress-strip__copy">
      <span class="progress-strip__heading">{{ heading }}</span>
      <span class="progress-strip__current">{{ currentLine }}</span>
    </span>
    <span class="progress-strip__count">{{ progress.done }}/{{ progress.total }}</span>
  </button>
</template>

<style scoped>
.progress-strip {
  display: flex;
  align-items: center;
  gap: 9px;
  width: 100%;
  margin-top: 8px;
  padding: 7px 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
  color: var(--text);
  text-align: left;
  cursor: pointer;
  transition: border-color var(--transition), background var(--transition);
}

.progress-strip:hover {
  border-color: color-mix(in srgb, var(--text) 18%, transparent);
}

.progress-strip:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}

.progress-strip__copy {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
}

.progress-strip__heading {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 11px;
  color: var(--muted);
}

.progress-strip__current {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 12.5px;
}

.progress-strip__count {
  flex-shrink: 0;
  font-size: 12px;
  color: var(--muted);
  font-variant-numeric: tabular-nums;
}
</style>
