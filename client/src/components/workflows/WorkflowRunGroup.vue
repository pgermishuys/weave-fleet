<script setup lang="ts">
import { computed } from "vue";
import { Workflow as WorkflowIcon } from "lucide-vue-next";
import type { SessionListItem } from "@/api/client";
import SessionItem from "@/components/sessions/SessionItem.vue";
import StatusGlyph from "@/components/sessions/StatusGlyph.vue";
import WorkflowStartedBy from "@/components/workflows/WorkflowStartedBy.vue";
import { runStatusLabel, type WorkflowRun } from "@/lib/workflows";

/**
 * A workflow run in the Sessions list: a header row with the run's title and status, and its step sessions under it,
 * each named by its step.
 */
const props = defineProps<{
  run: WorkflowRun | null;
  steps: { session: SessionListItem; label: string }[];
  activeSessionId: string | null;
}>();

const emit = defineEmits<{
  selectSession: [session: SessionListItem];
  dragSessionStart: [sessionId: string, projectId: string | null];
  dragSessionEnd: [];
}>();

const status = computed(() => (props.run ? runStatusLabel(props.run) : null));

/** "With you" on the step row of a step the user finishes, while it's open. */
function stepNote(sessionId: string): string | undefined {
  return props.run?.status === "running" && props.run.withYou?.sessionId === sessionId ? "With you" : undefined;
}
const title = computed(() => props.run?.title ?? props.steps[0]?.session.session.title?.split(" · ")[0] ?? "Workflow run");

/** The header opens the session that needs you, else the newest step. */
function openRun(): void {
  const target = props.run?.waiting?.sessionId
    ? props.steps.find((step) => step.session.session.id === props.run!.waiting!.sessionId)
    : props.steps.at(-1);
  if (target) emit("selectSession", target.session);
}
</script>

<template>
  <div
    class="wf-group"
    data-testid="workflow-run-group"
  >
    <button
      type="button"
      class="wf-group__head"
      :aria-label="`${title}, ${run?.workflowName ?? 'workflow run'}${status ? `, ${status.label}` : ''}`"
      @click="openRun"
    >
      <StatusGlyph
        v-if="run?.status === 'running'"
        status="active"
        label="Working"
      />
      <WorkflowIcon
        v-else
        class="wf-group__icon"
        aria-hidden="true"
      />
      <span class="wf-group__title">{{ title }}</span>
      <span
        v-if="status"
        class="wf-group__meta"
        :class="`wf-group__meta--${status.tone}`"
      >{{ status.label }}</span>
    </button>
    <div
      v-if="run?.startedBy"
      class="wf-group__by"
    >
      <WorkflowStartedBy :started-by="run.startedBy" />
    </div>
    <div
      v-for="step in steps"
      :key="step.session.session.id"
      class="wf-group__step"
    >
      <SessionItem
        :session="step.session"
        :label="step.label"
        :step-note="stepNote(step.session.session.id)"
        :active="step.session.session.id === activeSessionId"
        @select="emit('selectSession', $event)"
        @drag-session-start="(id, project) => emit('dragSessionStart', id, project)"
        @drag-session-end="emit('dragSessionEnd')"
      />
    </div>
  </div>
</template>

<style scoped>
.wf-group__by {
  display: flex;
  min-width: 0;
  padding: 0 10px 2px 30px;
}

.wf-group__head {
  display: flex;
  width: 100%;
  min-width: 0;
  min-height: 32px;
  align-items: center;
  gap: 9px;
  border: 0;
  border-radius: var(--radius-btn);
  padding: 0 10px;
  background: transparent;
  color: var(--text);
  cursor: pointer;
  font-weight: 500;
  text-align: left;
  transition: background var(--transition);
}

.wf-group__head:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
}

.wf-group__head:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.wf-group__icon {
  width: 13px;
  height: 13px;
  flex-shrink: 0;
  color: var(--accent);
}

.wf-group__title {
  flex: 1 1 auto;
  min-width: 0;
  overflow: hidden;
  font-size: 13px;
  line-height: 1.3;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.wf-group__meta {
  flex-shrink: 0;
  color: color-mix(in srgb, var(--muted) 80%, transparent);
  font-size: 12px;
  font-weight: 400;
  white-space: nowrap;
}

.wf-group__meta--wait {
  color: var(--idle);
}

.wf-group__meta--run {
  color: var(--running);
}

.wf-group__meta--with {
  color: var(--accent);
}

.wf-group__meta--fail {
  color: var(--error);
}

/* Steps sit under the run, indented past its icon. */
.wf-group__step {
  padding-left: 18px;
}
</style>
