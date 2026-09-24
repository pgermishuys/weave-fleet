<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { Check, UserRound, Workflow as WorkflowIcon } from "lucide-vue-next";
import StatusGlyph from "@/components/sessions/StatusGlyph.vue";
import WorkflowStartedBy from "@/components/workflows/WorkflowStartedBy.vue";
import { checkWithMeNote, ordinal, type WorkflowRunStep } from "@/lib/workflows";
import { useWorkflowsStore } from "@/stores/workflows";

/**
 * Every step of the run this session is a step of, with its state, under the session's header. A step that ran opens
 * its session; this session's step is marked, and so is every step the user finishes. The run line has the "Check with
 * me" switch, which applies from the next step.
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

const finished = computed(() => !run.value || ["done", "ended", "failed"].includes(run.value.status));
const note = computed(() => (run.value ? checkWithMeNote(run.value) : null));
const switching = shallowRef(false);
const error = shallowRef<string | null>(null);

async function toggleCheckWithMe(): Promise<void> {
  if (!run.value || finished.value || switching.value) return;
  switching.value = true;
  error.value = null;
  try {
    await store.setCheckWithMe(run.value.id, !run.value.checkWithMe);
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : "Couldn't change Check with me.";
  } finally {
    switching.value = false;
  }
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
      <WorkflowStartedBy
        v-if="run.startedBy"
        :started-by="run.startedBy"
      />
      <button
        v-if="!finished"
        type="button"
        class="wf-check"
        role="switch"
        :aria-checked="Boolean(run.checkWithMe)"
        :disabled="switching"
        title="Check with me after each step. Applies to the steps that haven't finished."
        data-testid="workflow-check-with-me"
        @click="toggleCheckWithMe"
      >
        <UserRound aria-hidden="true" />
        <b>Check with me</b>
        <span
          class="wf-check__switch"
          aria-hidden="true"
        />
      </button>
    </div>
    <p
      v-if="note && !finished"
      class="wf-stepper__note"
      data-testid="workflow-check-with-me-note"
    >
      {{ note }}
    </p>
    <p
      v-if="error"
      class="wf-stepper__error"
      role="alert"
    >
      {{ error }}
    </p>
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
          <UserRound
            v-if="step.withYou && step.state !== 'done' && step.state !== 'skipped'"
            class="wf-stp__you"
            aria-label="You finish this step"
            data-testid="workflow-step-with-you"
          />
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

.wf-stp__you {
  width: 12px;
  height: 12px;
  color: var(--accent);
}

.wf-check {
  display: inline-flex;
  flex-shrink: 0;
  align-items: center;
  gap: 5px;
  margin-left: auto;
  padding: 2px 4px 2px 8px;
  border: 1px solid var(--border);
  border-radius: 999px;
  background: transparent;
  color: var(--muted);
  cursor: pointer;
  font: inherit;
  font-size: 12px;
}

.wf-check b {
  color: var(--text);
  font-weight: 500;
}

.wf-check:disabled {
  cursor: default;
  opacity: 0.7;
}

.wf-check:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}

.wf-check__switch {
  position: relative;
  width: 26px;
  height: 15px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--text) 18%, transparent);
  transition: background var(--transition);
}

.wf-check__switch::after {
  position: absolute;
  top: 2px;
  left: 2px;
  width: 11px;
  height: 11px;
  border-radius: 999px;
  background: var(--bg);
  content: "";
  transition: transform var(--transition);
}

.wf-check[aria-checked="true"] .wf-check__switch {
  background: var(--accent);
}

.wf-check[aria-checked="true"] .wf-check__switch::after {
  transform: translateX(11px);
}

.wf-stepper__note,
.wf-stepper__error {
  margin: 0;
  color: var(--muted);
  font-size: 12px;
}

.wf-stepper__error {
  color: var(--error);
}

.wf-stp__extra {
  color: var(--muted);
  font-size: 10.5px;
}
</style>
