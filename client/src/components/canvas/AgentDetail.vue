<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { ArrowUpRight, CircleStop } from "lucide-vue-next";
import { useSessionStream } from "@/composables/use-session-stream";
import { readWorkOutput, stopWork } from "@/composables/use-running-work";
import { formatTokens } from "@/lib/format-utils";
import { useMachineTarget } from "@/lib/machine-target";
import { isWorkRunning, workResult } from "@/lib/running-work";
import { latestOutputLine, summarizeAgentActivity, type AgentRow } from "@/lib/session-lineage";
import { useSessionsStore } from "@/stores/sessions";

/**
 * The selected agent in the Agents tab: what it was asked, its tool calls and tokens, its latest output, and Open session
 * and Stop, each only when the harness allows it. Activity is read from the agent's own session, when it has one; a
 * work item without one shows what it was asked and, when its output can be read, the output's last line.
 */
const props = defineProps<{
  row: AgentRow;
  /** The session the row belongs to: a subagent's session opens with it as its parent. */
  parentSessionId: string;
  stopping?: boolean;
}>();

const machine = useMachineTarget();

const emit = defineEmits<{ model: [modelId: string | null] }>();

const router = useRouter();
const sessionsStore = useSessionsStore();

const childId = computed(() => props.row.sessionId ?? "");
const { messages, isLoading } = useSessionStream(childId, () => Boolean(childId.value));
const activity = computed(() => summarizeAgentActivity(messages.value));
watch(() => activity.value.modelId, (modelId) => emit("model", modelId), { immediate: true });

// A work item without a session of its own: its output, when the harness can give it.
const outputLine = shallowRef<string | null>(null);
watch(() => [props.row.work?.id, props.row.work?.canReadOutput, props.row.sessionId] as const, async ([workId, canRead, sessionId]) => {
  outputLine.value = null;
  if (!workId || !canRead || sessionId) return;
  const result = await readWorkOutput(machine, props.row.work!.sessionId, workId);
  if (result.ok && props.row.work?.id === workId) outputLine.value = latestOutputLine(result.page.output);
}, { immediate: true });

const asked = computed(() => props.row.work?.label ?? activity.value.asked);
const tools = computed(() => {
  const { toolCalls, topTools } = activity.value;
  if (toolCalls === 0) return null;
  const calls = toolCalls === 1 ? "1 call" : `${toolCalls} calls`;
  return topTools.length > 0 ? `${calls} · ${topTools.join(", ")}` : calls;
});
const tokens = computed(() => {
  const { tokensIn, tokensOut } = activity.value;
  return tokensIn + tokensOut > 0 ? `${formatTokens(tokensIn)} in · ${formatTokens(tokensOut)} out` : null;
});
const latest = computed(() => activity.value.latest ?? outputLine.value);
const result = computed(() => (props.row.work ? workResult(props.row.work) : null));

const canStop = computed(() => Boolean(props.row.work?.canStop && isWorkRunning(props.row.work)));
/** Running inside the call that started it, with no session and no Stop: only interrupting the turn ends it. */
const insideTurn = computed(() => Boolean(props.row.work && isWorkRunning(props.row.work) && !props.row.sessionId && !canStop.value));
const stopError = shallowRef<string | null>(null);

function sessionSearch(sessionId: string): { instanceId: string; parentSessionId: string | undefined } {
  const listed = sessionsStore.sessions.find((item) => item.session.id === sessionId);
  // A subagent's session is hidden from the list and opens with its parent; a fork or a started session stands alone.
  return { instanceId: listed?.instanceId ?? sessionId, parentSessionId: props.row.work ? props.parentSessionId : undefined };
}

const href = computed(() => {
  const sessionId = props.row.sessionId;
  if (!sessionId) return undefined;
  const { instanceId, parentSessionId } = sessionSearch(sessionId);
  const search = new URLSearchParams({ instanceId, ...(parentSessionId ? { parentSessionId } : {}) });
  return `/sessions/${encodeURIComponent(sessionId)}?${search.toString()}`;
});

function open(event: MouseEvent): void {
  const sessionId = props.row.sessionId;
  if (!sessionId || event.metaKey || event.ctrlKey || event.shiftKey || event.button !== 0) return;
  event.preventDefault();
  void router.navigate({ to: "/sessions/$id", params: { id: sessionId }, search: sessionSearch(sessionId) });
}

async function stop(): Promise<void> {
  const work = props.row.work;
  if (!work) return;
  stopError.value = null;
  const outcome = await stopWork(machine, work.sessionId, work.id);
  if (!outcome.ok) stopError.value = outcome.error;
}
</script>

<template>
  <dl
    class="agent-detail"
    data-testid="agents-detail"
  >
    <template v-if="asked">
      <dt>Asked to</dt>
      <dd :title="asked">
        {{ asked }}
      </dd>
    </template>
    <template v-if="tools">
      <dt>Tools</dt>
      <dd data-testid="agents-detail-tools">
        {{ tools }}
      </dd>
    </template>
    <template v-if="tokens">
      <dt>Tokens</dt>
      <dd data-testid="agents-detail-tokens">
        {{ tokens }}
      </dd>
    </template>
    <template v-if="result">
      <dt>Ended</dt>
      <dd>{{ result }}</dd>
    </template>
    <p
      v-if="latest"
      class="agent-detail__latest"
      :title="latest"
      data-testid="agents-detail-latest"
    >
      {{ latest }}
    </p>
    <p
      v-else-if="childId && isLoading"
      class="agent-detail__muted"
    >
      Reading its session…
    </p>
    <div
      v-if="href || canStop"
      class="agent-detail__actions"
    >
      <a
        v-if="href"
        class="agent-detail__button"
        :href="href"
        data-testid="agents-open-session"
        @click="open"
      >
        <ArrowUpRight aria-hidden="true" />
        Open session
      </a>
      <button
        v-if="canStop"
        type="button"
        class="agent-detail__button"
        :disabled="stopping"
        data-testid="agents-stop"
        @click="stop"
      >
        <CircleStop aria-hidden="true" />
        {{ stopping ? "Stopping…" : "Stop" }}
      </button>
    </div>
    <p
      v-else-if="insideTurn"
      class="agent-detail__muted"
    >
      Runs inside the tool call. Interrupt the turn to stop it.
    </p>
    <p
      v-if="stopError"
      class="agent-detail__error"
      role="alert"
    >
      {{ stopError }}
    </p>
  </dl>
</template>

<style scoped>
.agent-detail {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr);
  gap: 4px 14px;
  margin: 2px 0 6px 18px;
  padding: 10px 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
  font-size: 12.5px;
}

.agent-detail dt {
  color: var(--muted);
}

.agent-detail dd {
  min-width: 0;
  margin: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.agent-detail__latest {
  grid-column: 1 / 3;
  margin: 2px 0 0;
  padding: 6px 8px;
  overflow: hidden;
  border-radius: 6px;
  background: color-mix(in srgb, var(--text) 5%, transparent);
  color: var(--muted);
  font-family: var(--font-mono);
  font-size: 11.5px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.agent-detail__muted,
.agent-detail__error {
  grid-column: 1 / 3;
  margin: 2px 0 0;
  color: var(--muted);
  font-size: 12px;
}

.agent-detail__error {
  color: var(--error);
}

.agent-detail__actions {
  grid-column: 1 / 3;
  display: flex;
  gap: 6px;
  margin-top: 4px;
}

.agent-detail__button {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 28px;
  padding: 0 10px;
  border: 1px solid var(--border);
  border-radius: 7px;
  background: transparent;
  color: var(--text);
  font: inherit;
  font-size: 12.5px;
  text-decoration: none;
  cursor: pointer;
  transition: background var(--transition);
}

.agent-detail__button:hover:not(:disabled) {
  background: color-mix(in srgb, var(--text) 6%, transparent);
}

.agent-detail__button:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}

.agent-detail__button:disabled {
  opacity: 0.6;
  cursor: default;
}

.agent-detail__button svg {
  width: 13px;
  height: 13px;
}
</style>
