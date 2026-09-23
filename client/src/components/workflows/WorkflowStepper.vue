<script setup lang="ts">
import { computed } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { Check, Workflow as WorkflowIcon } from "lucide-vue-next";
import StatusGlyph from "@/components/sessions/StatusGlyph.vue";
import { ordinal, type WorkflowRunStep } from "@/lib/workflows";
import { useWorkflowsStore } from "@/stores/workflows";

/**
 * Every step of the run this session is a step of, with its state, under the session's header. A step that ran opens
 * its session; this session's step is marked.
 */
const props = defineProps<{ sessionId: string }>();

const router = useRouter();
const store = useWorkflowsStore();

const run = computed(() => store.runForSession(props.sessionId));
const mine = computed(() => run.value?.sessions.find((s) => s.sessionId === props.sessionId) ?? null);
const steps = computed(() => run.value?.steps.filter((step) => step.enabled) ?? []);

function extra(step: WorkflowRunStep): string | null {
  if (step.visits > 1) return `${ordinal(step.visits)}`;
  if (step.maxLoops && step.state === "pending") return `≤${step.maxLoops}×`;
  return null;
}

function open(step: WorkflowRunStep): void {
  if (step.sessionId && step.sessionId !== props.sessionId)
    void router.navigate({ to: "/sessions/$id", params: { id: step.sessionId }, search: { instanceId: undefined, parentSessionId: undefined } });
}
</script>

<template>
  <div
    v-if="run"
    class="wf-stepper"
    data-testid="workflow-stepper"
  >
    <div class="wf-stepper__run">
      <WorkflowIcon aria-hidden="true" />
      <span>Step of <b>{{ run.title }}</b> · {{ run.workflowName }}</span>
      <span
        v-if="run.branch"
        class="wf-stepper__branch"
      >{{ run.branch }}</span>
    </div>
    <ol class="wf-stepper__steps">
      <li
        v-for="(step, index) in steps"
        :key="step.id"
      >
        <span
          v-if="index > 0"
          class="wf-stepper__sep"
          aria-hidden="true"
        />
        <button
          type="button"
          class="wf-stp"
          :class="{
            'wf-stp--current': mine?.stepId === step.id,
            'wf-stp--wait': step.state === 'waiting',
            'wf-stp--run': step.state === 'running',
            'wf-stp--pending': step.state === 'pending',
          }"
          :disabled="!step.sessionId || step.sessionId === sessionId"
          :aria-current="mine?.stepId === step.id ? 'step' : undefined"
          :title="step.outcome ? `${step.title}: ${step.outcome}` : step.title"
          @click="open(step)"
        >
          <Check
            v-if="step.state === 'done'"
            class="wf-stp__ok"
            aria-hidden="true"
          />
          <StatusGlyph
            v-else-if="step.state === 'running'"
            status="active"
            label="Working"
          />
          <span
            v-else
            class="wf-stp__dot"
            aria-hidden="true"
          />
          {{ step.title }}
          <span
            v-if="extra(step)"
            class="wf-stp__extra"
          >{{ extra(step) }}</span>
        </button>
      </li>
    </ol>
  </div>
</template>

<style scoped>
.wf-stepper {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 8px 16px 10px;
  border-bottom: 1px solid var(--border);
}

.wf-stepper__run {
  display: flex;
  min-width: 0;
  align-items: center;
  gap: 6px;
  color: var(--muted);
  font-size: 12px;
}

.wf-stepper__run svg {
  width: 12px;
  height: 12px;
  flex-shrink: 0;
  color: var(--accent);
}

.wf-stepper__run b {
  color: var(--text);
  font-weight: 500;
}

.wf-stepper__branch {
  font-family: var(--font-mono);
  font-size: 11px;
}

.wf-stepper__steps {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 4px 0;
  margin: 0;
  padding: 0;
  list-style: none;
}

.wf-stepper__steps li {
  display: flex;
  align-items: center;
}

.wf-stepper__sep {
  width: 14px;
  height: 1px;
  margin: 0 2px;
  background: var(--border);
}

.wf-stp {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 3px 8px;
  border: 1px solid transparent;
  border-radius: 999px;
  background: transparent;
  color: var(--text);
  cursor: pointer;
  font-size: 12px;
  white-space: nowrap;
}

.wf-stp:disabled {
  cursor: default;
}

.wf-stp:not(:disabled):hover {
  background: color-mix(in srgb, var(--text) 6%, transparent);
}

.wf-stp--current {
  border-color: var(--border);
  background: color-mix(in srgb, var(--text) 6%, transparent);
  font-weight: 500;
}

.wf-stp--pending {
  color: var(--muted);
}

.wf-stp--wait {
  color: var(--idle);
}

.wf-stp__ok {
  width: 12px;
  height: 12px;
  color: var(--running);
}

.wf-stp__dot {
  width: 7px;
  height: 7px;
  border: 1.5px solid currentColor;
  border-radius: 999px;
  opacity: 0.6;
}

.wf-stp--wait .wf-stp__dot {
  border: 0;
  background: var(--idle);
  opacity: 1;
}

.wf-stp__extra {
  color: var(--muted);
  font-size: 10.5px;
}
</style>
