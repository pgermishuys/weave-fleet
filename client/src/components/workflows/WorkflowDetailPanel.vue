<script setup lang="ts">
import { computed } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { AlertTriangle, Bot, Check, Repeat, UserRound, Workflow as WorkflowIcon } from "lucide-vue-next";
import StatusGlyph from "@/components/sessions/StatusGlyph.vue";
import WorkflowRunBox from "@/components/workflows/WorkflowRunBox.vue";
import { useEnabledHarnesses } from "@/composables/use-enabled-harnesses";
import { useModelRoles } from "@/composables/use-model-roles";
import { useWorkflowsNav } from "@/composables/use-workflows-nav";
import {
  isRole,
  loopNotes,
  modelShortName,
  ROLE_NAMES,
  runStatusLabel,
  type WorkflowRun,
} from "@/lib/workflows";
import { useWorkflowsStore } from "@/stores/workflows";

const router = useRouter();
const nav = useWorkflowsNav();
const store = useWorkflowsStore();
const { choiceFor } = useModelRoles();
const { defaultHarnessType } = useEnabledHarnesses();

const workflow = computed(() => nav.activeWorkflow.value);
const loops = computed(() => (workflow.value ? loopNotes(workflow.value) : []));

/** "Strong · Opus 5.5, Standard · Sonnet 5", then pinned models as they are. */
const models = computed(() => {
  if (!workflow.value) return "";
  const seen = new Set<string>();
  const labels: string[] = [];
  for (const step of workflow.value.steps) {
    if (step.kind !== "agent" || !step.model || seen.has(step.model)) continue;
    seen.add(step.model);
    labels.push(isRole(step.model)
      ? `${ROLE_NAMES[step.model]} · ${modelShortName(choiceFor(defaultHarnessType.value, step.model).model)}`
      : step.model);
  }
  return labels.join(", ");
});

const skills = computed(() => [...new Set(workflow.value?.steps.map((s) => s.skill).filter((s): s is string => !!s) ?? [])]);

const runs = computed(() => store.orderedRuns.filter((run) => run.workflowId === workflow.value?.id).slice(0, 6));

function when(iso: string): string {
  const date = new Date(iso);
  const today = new Date();
  return date.toDateString() === today.toDateString()
    ? `Today ${date.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}`
    : date.toLocaleDateString([], { weekday: "short", day: "numeric", month: "short" });
}

function runText(run: WorkflowRun): string {
  if (run.status === "waiting" && run.waiting) return `Needs you · ${run.waiting.stepTitle}`;
  if (run.status === "running") {
    const step = run.steps.find((s) => s.id === run.currentStepId);
    return step ? `Running · ${step.title}` : "Running";
  }
  return runStatusLabel(run).label;
}

function openRun(run: WorkflowRun): void {
  const session = run.waiting?.sessionId ?? run.sessions.at(-1)?.sessionId;
  if (session) void router.navigate({ to: "/sessions/$id", params: { id: session }, search: { instanceId: undefined, parentSessionId: undefined } });
}
</script>

<template>
  <section
    class="wf-detail"
    aria-label="Workflow"
  >
    <div
      v-if="workflow"
      class="wf-detail__inner"
    >
      <header class="wf-detail__head">
        <span class="wf-detail__icon"><WorkflowIcon aria-hidden="true" /></span>
        <h2>{{ workflow.name }}</h2>
        <span
          class="wf-pill"
          :class="{ 'wf-pill--accent': !workflow.builtIn }"
        >{{ workflow.builtIn ? "Built into Fleet" : workflow.file }}</span>
      </header>

      <p
        v-if="workflow.description"
        class="wf-detail__blurb"
      >
        {{ workflow.description }}
      </p>

      <ul
        v-if="workflow.errors.length > 0"
        class="wf-detail__errors"
        role="alert"
      >
        <li
          v-for="error in workflow.errors"
          :key="error"
        >
          <AlertTriangle aria-hidden="true" />{{ error }}
        </li>
      </ul>

      <div v-if="workflow.steps.length > 0">
        <div class="wf-sec-label">
          Steps
        </div>
        <div
          class="wf-strip"
          data-testid="workflow-steps"
        >
          <template
            v-for="(step, index) in workflow.steps"
            :key="step.id"
          >
            <span
              v-if="index > 0"
              class="wf-strip__arrow"
              aria-hidden="true"
            >→</span>
            <span
              class="wf-chip"
              :class="{ 'wf-chip--you': step.kind === 'you', 'wf-chip--optional': step.optional }"
            >
              <UserRound
                v-if="step.kind === 'you'"
                aria-hidden="true"
              />
              <Bot
                v-else
                aria-hidden="true"
              />
              {{ step.title }}<span
                v-if="step.optional"
                class="wf-strip__arrow"
              >(optional)</span>
            </span>
          </template>
        </div>
        <div
          v-if="loops.length"
          class="wf-loops"
        >
          <span
            v-for="loop in loops"
            :key="loop"
          ><Repeat aria-hidden="true" />{{ loop }}</span>
        </div>
      </div>

      <dl class="wf-facts">
        <div>
          <dt>Starts from</dt>
          <dd>A sentence</dd>
        </div>
        <div>
          <dt>Runs in</dt>
          <dd>A new worktree from the base branch</dd>
        </div>
        <div>
          <dt>Models{{ skills.length ? " · skills" : "" }} (roles are set in Settings)</dt>
          <dd>
            {{ models }}
            <span
              v-if="skills.length"
              class="wf-facts__mono"
            >{{ skills.join(", ") }}</span>
          </dd>
        </div>
      </dl>

      <div>
        <div class="wf-sec-label">
          Recent runs
        </div>
        <div
          v-if="runs.length"
          class="wf-runs"
        >
          <button
            v-for="run in runs"
            :key="run.id"
            type="button"
            class="wf-runs__row"
            data-testid="workflow-recent-run"
            @click="openRun(run)"
          >
            <span class="wf-runs__glyph">
              <StatusGlyph
                v-if="run.status === 'running'"
                status="active"
                label="Running"
              />
              <span
                v-else-if="run.status === 'waiting'"
                class="wf-dot-wait"
                aria-label="Needs you"
              />
              <AlertTriangle
                v-else-if="run.status === 'failed'"
                class="wf-runs__fail"
                aria-hidden="true"
              />
              <Check
                v-else
                class="wf-runs__ok"
                aria-hidden="true"
              />
            </span>
            <span class="wf-runs__title">{{ run.title }}</span>
            <span class="wf-runs__when">{{ when(run.createdAt) }}</span>
            <span
              class="wf-runs__status"
              :class="`wf-runs__status--${runStatusLabel(run).tone}`"
            >{{ runText(run) }}</span>
          </button>
        </div>
        <p
          v-else
          class="wf-detail__none"
        >
          No runs yet.
        </p>
      </div>

      <WorkflowRunBox :workflow="workflow" />
    </div>

    <p
      v-else-if="nav.libraryError.value"
      class="wf-detail__none wf-detail__none--center"
      role="alert"
    >
      {{ nav.libraryError.value }}
    </p>
  </section>
</template>

<style scoped>
.wf-detail {
  display: flex;
  flex: 1;
  min-height: 0;
  justify-content: center;
  overflow-y: auto;
}

.wf-detail__inner {
  display: flex;
  width: 100%;
  max-width: 760px;
  flex-direction: column;
  gap: 20px;
  padding: 28px 24px 40px;
}

.wf-detail__head {
  display: flex;
  align-items: center;
  gap: 10px;
}

.wf-detail__head h2 {
  margin: 0;
  font-size: 18px;
  font-weight: 600;
}

.wf-detail__icon {
  display: grid;
  width: 28px;
  height: 28px;
  place-items: center;
  border-radius: var(--radius-btn);
  background: color-mix(in srgb, var(--accent) 14%, transparent);
  color: var(--accent);
}

.wf-detail__icon svg {
  width: 15px;
  height: 15px;
}

.wf-pill {
  padding: 2px 8px;
  border: 1px solid var(--border);
  border-radius: 999px;
  color: var(--muted);
  font-size: 11px;
  white-space: nowrap;
}

.wf-pill--accent {
  border-color: color-mix(in srgb, var(--accent) 45%, var(--border));
  color: var(--accent);
  font-family: var(--font-mono);
}

.wf-detail__blurb {
  margin: 0;
  color: color-mix(in srgb, var(--text) 85%, transparent);
  font-size: 14px;
  line-height: 1.55;
}

.wf-detail__errors {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin: 0;
  padding: 10px 12px;
  border: 1px solid color-mix(in srgb, var(--error) 40%, var(--border));
  border-radius: var(--radius-card);
  list-style: none;
  color: var(--error);
  font-family: var(--font-mono);
  font-size: 12px;
}

.wf-detail__errors li {
  display: flex;
  gap: 8px;
}

.wf-detail__errors svg {
  width: 13px;
  height: 13px;
  flex-shrink: 0;
  margin-top: 2px;
}

.wf-sec-label {
  margin-bottom: 8px;
  color: var(--muted);
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.05em;
  text-transform: uppercase;
}

.wf-strip {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
}

.wf-strip__arrow {
  color: var(--muted);
  font-size: 12px;
}

.wf-chip {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 4px 9px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
  font-size: 12.5px;
}

.wf-chip svg {
  width: 12px;
  height: 12px;
  color: var(--muted);
}

.wf-chip--you {
  border-color: color-mix(in srgb, var(--idle) 45%, var(--border));
}

.wf-chip--you svg {
  color: var(--idle);
}

.wf-chip--optional {
  border-style: dashed;
}

.wf-loops {
  display: flex;
  flex-wrap: wrap;
  gap: 14px;
  margin-top: 8px;
  color: var(--muted);
  font-size: 12px;
}

.wf-loops span {
  display: inline-flex;
  align-items: center;
  gap: 5px;
}

.wf-loops svg {
  width: 12px;
  height: 12px;
}

.wf-facts {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  gap: 12px;
  margin: 0;
}

.wf-facts dt {
  margin-bottom: 3px;
  color: var(--muted);
  font-size: 11.5px;
}

.wf-facts dd {
  margin: 0;
  font-size: 13px;
}

.wf-facts__mono {
  display: block;
  margin-top: 2px;
  color: var(--muted);
  font-family: var(--font-mono);
  font-size: 11px;
}

.wf-runs {
  display: flex;
  flex-direction: column;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  overflow: hidden;
}

.wf-runs__row {
  display: flex;
  align-items: center;
  gap: 10px;
  min-height: 34px;
  padding: 0 12px;
  border: 0;
  background: transparent;
  color: var(--text);
  cursor: pointer;
  font-size: 13px;
  text-align: left;
}

.wf-runs__row + .wf-runs__row {
  border-top: 1px solid var(--border);
}

.wf-runs__row:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
}

.wf-runs__glyph {
  display: grid;
  width: 16px;
  place-items: center;
}

.wf-runs__ok {
  width: 14px;
  height: 14px;
  color: var(--running);
}

.wf-runs__fail {
  width: 13px;
  height: 13px;
  color: var(--error);
}

.wf-runs__title {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.wf-runs__when,
.wf-runs__status {
  flex-shrink: 0;
  color: var(--muted);
  font-size: 12px;
}

.wf-runs__status--wait {
  color: var(--idle);
}

.wf-runs__status--run {
  color: var(--running);
}

.wf-runs__status--fail {
  color: var(--error);
}

.wf-dot-wait {
  width: 8px;
  height: 8px;
  border-radius: 999px;
  background: var(--idle);
}

.wf-detail__none {
  margin: 0;
  color: var(--muted);
  font-size: 12.5px;
}

.wf-detail__none--center {
  align-self: center;
  padding: 24px;
}
</style>
