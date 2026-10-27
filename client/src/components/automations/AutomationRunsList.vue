<script setup lang="ts">
import { computed } from "vue";
import StatusGlyph from "@/components/sessions/StatusGlyph.vue";
import { describeDate } from "@/lib/automation-schedule";
import { describeRunState, describeRunTrigger } from "@/lib/automations";
import type { AutomationRun } from "@/stores/automations";

const props = defineProps<{ runs: readonly AutomationRun[] }>();
const emit = defineEmits<{ open: [run: AutomationRun] }>();

const doneCount = computed(() => props.runs.filter((run) => run.state === "done").length);

/** When a run was for: its schedule time, or when it started. */
function runTime(run: AutomationRun): string {
  return describeDate(new Date(run.scheduledFor ?? run.startedAt));
}

/** What a run did, in words (a dreaming night's summary); a failure or skip also says why unless the summary already does. */
function runNotes(run: AutomationRun): string[] {
  const why = run.error && (!run.summary || run.state === "failed") ? [run.error] : [];
  return run.summary ? [run.summary, ...why] : why;
}
</script>

<template>
  <section
    class="automation-runs"
    aria-label="Runs"
  >
    <div class="automation-runs__label">
      <span>Runs</span>
      <span>{{ doneCount }} of {{ runs.length }} finished</span>
    </div>
    <component
      :is="run.sessionId ? 'button' : 'div'"
      v-for="run in runs"
      :key="run.id"
      :type="run.sessionId ? 'button' : undefined"
      class="automation-run"
      :class="{ 'automation-run--openable': run.sessionId }"
      data-testid="automation-run"
      @click="emit('open', run)"
    >
      <span class="automation-run__glyph">
        <StatusGlyph
          v-if="run.state === 'running' || run.state === 'starting'"
          status="active"
          :label="describeRunState(run).label"
        />
        <StatusGlyph
          v-else-if="run.state === 'waiting'"
          status="waiting_input"
          label="Needs you"
        />
        <StatusGlyph
          v-else-if="run.state === 'failed'"
          status="error"
          label="Failed"
        />
      </span>
      <span class="automation-run__title">
        {{ runTime(run) }}
        <small v-if="describeRunTrigger(run.trigger)">{{ describeRunTrigger(run.trigger) }}</small>
        <small
          v-if="run.machineName"
          class="automation-run__machine"
          data-testid="automation-run-machine"
        >on {{ run.machineName }}</small>
      </span>
      <span class="automation-run__end">
        <span
          v-if="run.sessionId"
          class="automation-run__open"
        >{{ run.workflowRunId ? "Open run →" : "Open session →" }}</span>
        <span
          class="automation-run__state"
          :class="`automation-run__state--${describeRunState(run).tone}`"
        >{{ describeRunState(run).label }}</span>
      </span>
      <span
        v-for="note in runNotes(run)"
        :key="note"
        class="automation-run__why"
        data-testid="automation-run-note"
      >{{ note }}</span>
    </component>
  </section>
</template>

<style scoped>
.automation-runs {
  width: 100%;
  max-width: 760px;
  margin: 0 auto;
}

.automation-runs__label {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  padding: 0 10px 6px;
  color: var(--muted);
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.05em;
  text-transform: uppercase;
}

.automation-runs__label span:last-child {
  font-size: 12px;
  font-weight: 400;
  letter-spacing: 0;
  text-transform: none;
}

.automation-run {
  display: grid;
  width: 100%;
  min-height: 34px;
  grid-template-columns: 10px minmax(0, 1fr) auto;
  align-items: center;
  column-gap: 9px;
  border: 0;
  border-radius: var(--radius-btn);
  padding: 5px 10px;
  background: transparent;
  color: var(--text);
  font: inherit;
  text-align: left;
}

.automation-run--openable {
  cursor: pointer;
}

.automation-run--openable:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
}

.automation-run--openable:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.automation-run__glyph {
  display: grid;
  width: 10px;
  place-items: center;
}

.automation-run__title {
  font-size: 13px;
  font-variant-numeric: tabular-nums;
}

.automation-run__title small {
  margin-left: 6px;
  color: var(--muted);
  font-size: 12px;
}

/* The machine a run went to, in the sidebar's machine coral. */
.automation-run__title .automation-run__machine {
  color: var(--coral);
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
}

.automation-run__end {
  display: flex;
  align-items: center;
  gap: 10px;
}

.automation-run__open {
  color: var(--muted);
  font-size: 12px;
  opacity: 0;
  transition: opacity var(--transition);
}

.automation-run:hover .automation-run__open,
.automation-run:focus-visible .automation-run__open {
  opacity: 1;
}

.automation-run__state {
  color: color-mix(in srgb, var(--muted) 85%, transparent);
  font-size: 12px;
  white-space: nowrap;
}

.automation-run__state--working {
  color: var(--running);
}

.automation-run__state--error {
  color: var(--error);
}

.automation-run__state--warn {
  color: var(--status-waiting);
}

.automation-run__why {
  grid-column: 2 / 4;
  color: var(--muted);
  font-size: 12px;
}
</style>
