<script setup lang="ts">
import { computed } from "vue";
import { Check, Square, X } from "lucide-vue-next";
import { formatElapsed, workElapsedMs } from "@/lib/running-work";
import { lineageKindLabel, type AgentRow } from "@/lib/session-lineage";

/**
 * One agent in the Agents tab: a status dot, its name and task, then its kind, harness and model, and how long it has
 * run or how it ended. Selecting it shows its detail under it.
 */
const props = defineProps<{
  row: AgentRow;
  now: number;
  /** "Claude Code · Sonnet 5.5", or whatever of it Fleet knows. */
  runsOn?: string | null;
  /** Said after the harness and model: "asked a question". */
  note?: string | null;
  selected?: boolean;
  /** Rows that only link elsewhere (the parent) aren't selectable. */
  expandable?: boolean;
  /** The parent's row: no kind, it isn't one of this session's agents. */
  parent?: boolean;
}>();

const emit = defineEmits<{ select: [] }>();

const kindLabel = computed(() => (props.row.kind === "task" ? "task" : lineageKindLabel(props.row.kind)));

const stateText = computed(() => {
  const { row } = props;
  switch (row.state) {
    case "running":
      return row.work ? formatElapsed(workElapsedMs(row.work, props.now)) : "working";
    case "waiting": return "needs you";
    case "failed": return row.work?.endedReason === "lost" ? "lost" : "failed";
    case "stopped": return "stopped";
    case "done": return row.work ? formatElapsed(workElapsedMs(row.work, props.now)) : "done";
    default: return "idle";
  }
});

const stateTitle = computed(() => {
  const work = props.row.work;
  if (!work) return undefined;
  const started = new Date(work.startedAt).toLocaleTimeString();
  return props.row.state === "running" ? `Started at ${started}` : `Ran for ${stateText.value}, from ${started}`;
});

const label = computed(() => [props.row.name, props.row.task, props.parent ? null : kindLabel.value, props.runsOn, props.note, stateText.value]
  .filter(Boolean)
  .join(", "));
</script>

<template>
  <button
    type="button"
    class="agent-row"
    :class="[`agent-row--${row.state}`, { 'agent-row--selected': selected }]"
    :aria-expanded="expandable ? selected : undefined"
    :aria-label="label"
    data-testid="agents-row"
    :data-kind="row.kind"
    :data-state="row.state"
    @click="emit('select')"
  >
    <span
      class="agent-row__mark"
      aria-hidden="true"
    >
      <Check
        v-if="row.state === 'done'"
        class="agent-row__icon agent-row__icon--done"
      />
      <X
        v-else-if="row.state === 'failed'"
        class="agent-row__icon agent-row__icon--failed"
      />
      <Square
        v-else-if="row.state === 'stopped'"
        class="agent-row__icon"
      />
      <span
        v-else
        class="agent-row__dot"
      />
    </span>
    <span class="agent-row__line">
      <span class="agent-row__name">{{ row.name }}</span>
      <span
        v-if="row.task"
        class="agent-row__task"
      >{{ row.task }}</span>
    </span>
    <span
      class="agent-row__state"
      :title="stateTitle"
    >{{ stateText }}</span>
    <span class="agent-row__sub">
      <span
        v-if="!parent"
        class="agent-row__kind"
      >{{ kindLabel }}</span>
      <span
        v-if="runsOn"
        class="agent-row__runs-on"
      >{{ runsOn }}</span>
      <span
        v-if="note"
        class="agent-row__note"
      >{{ runsOn ? "· " : "" }}{{ note }}</span>
    </span>
  </button>
</template>

<style scoped>
.agent-row {
  display: grid;
  grid-template-columns: 8px minmax(0, 1fr) auto;
  column-gap: 10px;
  row-gap: 1px;
  align-items: center;
  width: 100%;
  padding: 7px 8px;
  border: 0;
  border-radius: 8px;
  background: transparent;
  color: var(--text);
  font: inherit;
  font-size: 13px;
  text-align: left;
  cursor: pointer;
  transition: background var(--transition);
}

.agent-row:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
}

.agent-row--selected,
.agent-row--selected:hover {
  background: var(--card-bg);
}

.agent-row:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.agent-row__mark {
  display: grid;
  place-items: center;
  width: 8px;
  height: 8px;
}

.agent-row__dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  border: 1.5px solid var(--muted);
}

.agent-row--running .agent-row__dot {
  border: 0;
  background: var(--running);
  animation: agent-row-pulse 2s infinite;
}

.agent-row--waiting .agent-row__dot {
  border: 0;
  background: var(--status-waiting);
}

.agent-row__icon {
  width: 12px;
  height: 12px;
  flex-shrink: 0;
  color: var(--muted);
}

.agent-row__icon--done {
  color: var(--running);
}

.agent-row__icon--failed {
  color: var(--error);
}

.agent-row__line {
  display: flex;
  align-items: baseline;
  gap: 6px;
  min-width: 0;
}

.agent-row__name {
  min-width: 0;
  flex-shrink: 0;
  max-width: 100%;
  overflow: hidden;
  font-weight: 500;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.agent-row__task {
  min-width: 0;
  overflow: hidden;
  color: var(--muted);
  font-size: 12.5px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.agent-row__state {
  color: var(--muted);
  font-size: 12px;
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}

.agent-row--running .agent-row__state {
  color: var(--running);
}

.agent-row--waiting .agent-row__state {
  color: var(--status-waiting);
}

.agent-row--failed .agent-row__state {
  color: var(--error);
}

.agent-row__sub {
  grid-column: 2 / 4;
  display: flex;
  gap: 6px;
  min-width: 0;
  overflow: hidden;
  color: var(--muted);
  font-size: 12px;
  white-space: nowrap;
}

.agent-row__kind {
  flex-shrink: 0;
  font-size: 10.5px;
  font-weight: 600;
  letter-spacing: 0.04em;
  line-height: 18px;
  text-transform: uppercase;
}

.agent-row__runs-on,
.agent-row__note {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
}

.agent-row__note {
  flex-shrink: 0;
}

@keyframes agent-row-pulse {
  0%, 100% { opacity: 1; }
  50% { opacity: 0.45; }
}

@media (prefers-reduced-motion: reduce) {
  .agent-row--running .agent-row__dot {
    animation: none;
  }
}
</style>
