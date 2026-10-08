<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { useRouter } from "@tanstack/vue-router";
import {
  Activity,
  ArrowDown,
  ArrowUpRight,
  Ban,
  Bot,
  Check,
  ChevronDown,
  CircleAlert,
  CircleStop,
  ListTodo,
  ScrollText,
  Terminal,
  X,
} from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import WorkOutput from "@/components/session/WorkOutput.vue";
import { stopWork } from "@/composables/use-running-work";
import { dispatchCommandEvent } from "@/lib/command-events";
import { useMachineTarget } from "@/lib/machine-target";
import {
  formatAgo,
  formatElapsed,
  isWorkRunning,
  workElapsedMs,
  workFailed,
  workKindLabel,
  workResult,
  type RunningWorkItem,
} from "@/lib/running-work";
import { useSessionsStore } from "@/stores/sessions";

defineOptions({
  name: "BackgroundWorkRow",
});

/**
 * One piece of work in the background strip (and the status bar's list): kind, what it is, how long it has run, and
 * what can be done with it. Only what the harness offers shows: Output when its output can be read, Open when it runs
 * in a session of its own, Events for a monitor, Stop when it can be stopped on its own. A subagent with no session of
 * its own (Pi's subagent extension runs them inside the tool call) has Details instead of Open.
 */
const props = defineProps<{
  item: RunningWorkItem;
  /** The time elapsed counts to. */
  now: number;
  /** The model it runs on, for a subagent, when Fleet knows it. */
  model?: string | null;
  /** Whether its Stop is on its way. */
  stopping?: boolean;
}>();

const machine = useMachineTarget();

const router = useRouter();
const sessionsStore = useSessionsStore();

const outputOpen = shallowRef(false);
const detailsOpen = shallowRef(false);
const stopError = shallowRef<string | null>(null);

const running = computed(() => isWorkRunning(props.item));
const failed = computed(() => workFailed(props.item));
const result = computed(() => workResult(props.item));
const kindLabel = computed(() => workKindLabel(props.item.kind));
/** A shell's label is its command, shown as code; anything else reads as words. */
const isCommand = computed(() => props.item.kind === "shell");
const what = computed(() => {
  const { kind, title, label } = props.item;
  if (kind === "shell") return label ?? title;
  // A subagent or task: its agent, then what it was asked to do.
  if (kind === "subagent" || kind === "task") return label && label !== title ? `${title} · ${label}` : label ?? title;
  return label ?? title;
});
const time = computed(() => {
  if (running.value) return formatElapsed(workElapsedMs(props.item, props.now));
  const ended = props.item.endedAt ? Date.parse(props.item.endedAt) : Number.NaN;
  return Number.isNaN(ended) ? "" : formatAgo(Math.max(0, props.now - ended));
});
const timeTitle = computed(() => {
  const started = new Date(props.item.startedAt).toLocaleTimeString();
  return running.value
    ? `Started at ${started}`
    : `Ran for ${formatElapsed(workElapsedMs(props.item, props.now))}, from ${started}`;
});
const name = computed(() => props.item.label ?? props.item.title);
/** What the harness says about it while it runs (a chain's step, its model); once it ended, that's its result. */
const runningDetail = computed(() => (running.value ? props.item.detail ?? null : null));
/** A subagent with no session to open: what Fleet knows of it is all there is, so it shows here. */
const hasDetails = computed(() => props.item.kind === "subagent" && !props.item.childSessionId);

const childHref = computed(() => {
  const childId = props.item.childSessionId;
  if (!childId) return undefined;
  const instanceId = sessionsStore.sessions.find((session) => session.session.id === childId)?.instanceId ?? childId;
  const search = new URLSearchParams({ instanceId, parentSessionId: props.item.sessionId });
  return `/sessions/${encodeURIComponent(childId)}?${search.toString()}`;
});

function openChild(event: MouseEvent): void {
  const childId = props.item.childSessionId;
  // A modified click opens it elsewhere, as a link would.
  if (!childId || event.metaKey || event.ctrlKey || event.shiftKey || event.button !== 0) return;
  event.preventDefault();
  const instanceId = sessionsStore.sessions.find((session) => session.session.id === childId)?.instanceId ?? childId;
  void router.navigate({
    to: "/sessions/$id",
    params: { id: childId },
    search: { instanceId, parentSessionId: props.item.sessionId },
  });
}

/** A monitor's events are in the conversation, after the call that set it. */
function showEvents(): void {
  if (!props.item.toolCallId) return;
  dispatchCommandEvent("weave:command-show-message", { sessionId: props.item.sessionId, toolCallId: props.item.toolCallId });
}

async function stop(): Promise<void> {
  stopError.value = null;
  const outcome = await stopWork(machine, props.item.sessionId, props.item.id);
  if (!outcome.ok) stopError.value = outcome.error;
}
</script>

<template>
  <li
    class="work-row"
    :class="{ 'work-row--ended': !running, 'work-row--failed': failed }"
    data-testid="background-work-row"
    :data-kind="item.kind"
    :data-status="running ? 'running' : 'ended'"
  >
    <div class="work-row__line">
      <template v-if="running">
        <Terminal
          v-if="item.kind === 'shell'"
          class="work-row__icon"
          aria-hidden="true"
        />
        <Bot
          v-else-if="item.kind === 'subagent'"
          class="work-row__icon"
          aria-hidden="true"
        />
        <Activity
          v-else-if="item.kind === 'monitor'"
          class="work-row__icon"
          aria-hidden="true"
        />
        <ListTodo
          v-else
          class="work-row__icon"
          aria-hidden="true"
        />
      </template>
      <X
        v-else-if="failed && item.endedReason !== 'lost'"
        class="work-row__icon work-row__icon--failed"
        aria-hidden="true"
      />
      <CircleAlert
        v-else-if="item.endedReason === 'lost'"
        class="work-row__icon work-row__icon--lost"
        aria-hidden="true"
      />
      <Ban
        v-else-if="item.status === 'cancelled'"
        class="work-row__icon"
        aria-hidden="true"
      />
      <Check
        v-else
        class="work-row__icon work-row__icon--ok"
        aria-hidden="true"
      />

      <span class="work-row__kind">{{ kindLabel }}</span>
      <span
        class="work-row__what"
        :title="what"
      >
        <code v-if="isCommand">{{ what }}</code>
        <template v-else>{{ what }}</template>
        <span
          v-if="model && running"
          class="work-row__meta"
          data-testid="background-work-model"
        > · {{ model }}</span>
        <span
          v-if="runningDetail"
          class="work-row__meta"
          data-testid="background-work-detail"
        > · {{ runningDetail }}</span>
        <span
          v-if="result"
          class="work-row__meta"
          data-testid="background-work-result"
        > · {{ result }}</span>
      </span>
      <span
        class="work-row__time"
        :title="timeTitle"
      >{{ time }}</span>

      <Button
        v-if="item.canReadOutput"
        variant="ghost"
        size="sm"
        class="work-row__action"
        data-testid="background-work-output"
        :aria-expanded="outputOpen"
        :title="running ? 'Show the output so far' : 'Show the output'"
        @click="outputOpen = !outputOpen"
      >
        <ScrollText
          class="size-3.5"
          aria-hidden="true"
        />
        Output
      </Button>
      <Button
        v-if="childHref"
        as="a"
        :href="childHref"
        variant="ghost"
        size="sm"
        class="work-row__action"
        data-testid="background-work-open"
        title="Open its session"
        @click="openChild"
      >
        <ArrowUpRight
          class="size-3.5"
          aria-hidden="true"
        />
        Open
      </Button>
      <Button
        v-if="hasDetails"
        variant="ghost"
        size="sm"
        class="work-row__action"
        data-testid="background-work-details"
        :aria-expanded="detailsOpen"
        title="What it was asked, and how it is doing"
        @click="detailsOpen = !detailsOpen"
      >
        <ChevronDown
          class="size-3.5 work-row__chevron"
          :class="{ 'work-row__chevron--open': detailsOpen }"
          aria-hidden="true"
        />
        Details
      </Button>
      <Button
        v-if="item.kind === 'monitor' && item.toolCallId"
        variant="ghost"
        size="sm"
        class="work-row__action"
        data-testid="background-work-events"
        title="Jump to its events in the conversation"
        @click="showEvents"
      >
        <ArrowDown
          class="size-3.5"
          aria-hidden="true"
        />
        Events
      </Button>
      <Button
        v-if="running && item.canStop"
        variant="ghost"
        size="sm"
        class="work-row__action work-row__stop"
        data-testid="background-work-stop"
        :disabled="stopping"
        :aria-label="`Stop ${name}`"
        :title="stopping ? 'Stopping…' : 'Stop'"
        @click="stop"
      >
        <CircleStop
          class="size-3.5"
          aria-hidden="true"
        />
        <span class="work-row__stop-word">Stop</span>
      </Button>
      <span
        v-else
        class="work-row__stop-slot"
        aria-hidden="true"
      />
    </div>
    <p
      v-if="stopError"
      class="work-row__error"
      role="alert"
    >
      {{ stopError }}
    </p>
    <dl
      v-if="detailsOpen && hasDetails"
      class="work-row__details"
      data-testid="background-work-details-panel"
    >
      <template v-if="item.label">
        <dt>Asked to</dt>
        <dd>{{ item.label }}</dd>
      </template>
      <template v-if="item.detail">
        <dt>{{ running ? "Now" : "Ended" }}</dt>
        <dd>{{ item.detail }}</dd>
      </template>
      <p v-if="running && !item.canStop">
        Runs inside the tool call. Interrupt the turn to stop it.
      </p>
    </dl>
    <WorkOutput
      v-if="outputOpen && item.canReadOutput"
      :item="item"
    />
  </li>
</template>

<style scoped>
.work-row {
  min-width: 0;
  border-radius: 7px;
}

.work-row__line {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
  min-height: 28px;
  padding: 0 4px 0 8px;
  border-radius: 7px;
  font-size: 12px;
}

.work-row__line:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
}

.work-row__icon {
  flex-shrink: 0;
  width: 14px;
  height: 14px;
  color: var(--muted);
}

.work-row__icon--ok {
  color: var(--running);
}

.work-row__icon--failed {
  color: var(--error);
}

.work-row__icon--lost {
  color: var(--status-waiting);
}

.work-row__kind {
  flex-shrink: 0;
  width: 66px;
  color: var(--muted);
  font-size: 11px;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.work-row__what {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  color: var(--text);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.work-row__what code {
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
}

.work-row__meta,
.work-row__time {
  color: var(--muted);
}

.work-row__time {
  flex-shrink: 0;
  font-variant-numeric: tabular-nums;
}

.work-row--ended .work-row__what {
  color: var(--muted);
}

.work-row__action {
  flex-shrink: 0;
  height: 24px;
  padding: 0 8px;
  gap: 4px;
  color: var(--muted);
  font-size: 12px;
}

.work-row__action:hover,
.work-row__action:focus-visible {
  color: var(--text);
}

.work-row__stop {
  width: 58px;
}

/* Rows line up whether or not they offer Stop. */
.work-row__stop-slot {
  flex-shrink: 0;
  width: 58px;
}

.work-row__chevron {
  transition: transform 120ms ease;
}

.work-row__chevron--open {
  transform: rotate(180deg);
}

.work-row__details {
  display: grid;
  grid-template-columns: auto 1fr;
  gap: 2px 12px;
  margin: 2px 4px 6px 30px;
  padding: 8px 10px;
  border: 1px solid var(--border);
  border-radius: 7px;
  font-size: 12px;
}

.work-row__details dt {
  color: var(--muted);
}

.work-row__details dd {
  min-width: 0;
  margin: 0;
  overflow-wrap: anywhere;
  color: var(--text);
}

.work-row__details p {
  grid-column: 1 / -1;
  margin: 2px 0 0;
  color: var(--muted);
}

.work-row__error {
  margin: 0 4px 4px 30px;
  color: var(--error);
  font-size: 12px;
}

/* Narrow (a phone): the icon says the kind. */
@container background-work (max-width: 440px) {
  .work-row__kind {
    display: none;
  }

  .work-row__action {
    padding: 0 6px;
  }

  /* Stop keeps its icon and its tooltip; the word goes with the kind's. */
  .work-row__stop-word {
    display: none;
  }

  .work-row__stop,
  .work-row__stop-slot {
    width: 28px;
  }
}
</style>
