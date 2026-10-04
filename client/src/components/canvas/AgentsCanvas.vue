<script setup lang="ts">
import { computed, onMounted, shallowRef, watch } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { ChevronDown } from "lucide-vue-next";
import type { SessionListItem } from "@/api/client";
import AgentDetail from "@/components/canvas/AgentDetail.vue";
import AgentsCanvasRow from "@/components/canvas/AgentsCanvasRow.vue";
import { useHarnesses } from "@/composables/use-harnesses";
import { useModels } from "@/composables/use-models";
import { useSessionLineage } from "@/composables/use-session-lineage";
import { modelDisplayName } from "@/lib/turns";
import { lineageParentNote, sessionAgentState, type AgentRow } from "@/lib/session-lineage";
import { useSessionsStore } from "@/stores/sessions";

/**
 * The Agents tab: the session's lineage. The session it came from, what runs now (its subagents and tasks, and sessions
 * it started that are working or waiting on you), the sessions it forked or started, and the subagents that ended,
 * folded away. Selecting a row shows what it was asked, its tool calls and tokens, its latest output, and Open session
 * and Stop when the harness allows them. Shells and monitors stay in the background strip above the composer.
 */
const props = defineProps<{
  sessionId: string;
}>();

const router = useRouter();
const sessionsStore = useSessionsStore();
const { harnesses } = useHarnesses();
const { models } = useModels(() => props.sessionId);
const { parent, parentSession, parentTitle, agents, loadHistory, work } = useSessionLineage(() => props.sessionId);

onMounted(() => void loadHistory());
watch(() => props.sessionId, () => void loadHistory());

const self = computed(() => sessionsStore.sessions.find((item) => item.session.id === props.sessionId) ?? null);

const earlierOpen = shallowRef(false);
const selectedKey = shallowRef<string | null>(null);
/** Models read from a selected agent's own session, by row, so its row can name it. */
const seenModels = shallowRef<Record<string, string>>({});

// The first running agent is open to begin with, until you pick another.
const shownKey = computed(() => selectedKey.value ?? agents.value.running.find((row) => row.work)?.key ?? null);

function select(row: AgentRow): void {
  selectedKey.value = shownKey.value === row.key ? "" : row.key;
}

function rememberModel(row: AgentRow, modelId: string | null): void {
  if (modelId && seenModels.value[row.key] !== modelId) seenModels.value = { ...seenModels.value, [row.key]: modelId };
}

function harnessName(type: string | null | undefined): string | null {
  if (!type) return null;
  return harnesses.value.find((harness) => harness.type === type)?.displayName ?? type;
}

function sessionModelId(item: SessionListItem | null | undefined): string | null {
  return item?.selectedModel?.modelID ?? item?.lastAssistantModelId ?? null;
}

/** "Claude Code · Sonnet 5.5": a subagent runs in its parent's harness; a session in its own. */
function runsOn(row: AgentRow): string | null {
  const harness = harnessName(row.session?.harnessType ?? self.value?.harnessType);
  const modelId = seenModels.value[row.key] ?? sessionModelId(row.session);
  const model = modelId ? modelDisplayName(modelId, models.value) : null;
  return [harness, model].filter(Boolean).join(" · ") || null;
}

function note(row: AgentRow): string | null {
  if (row.state === "waiting") return "asked a question";
  if (row.work && !row.sessionId && row.state === "running") return "no session of its own";
  return null;
}

const parentRow = computed<AgentRow | null>(() => {
  const link = parent.value;
  if (!link) return null;
  const item = parentSession.value;
  return {
    key: `parent:${link.parentId}`,
    kind: link.kind,
    name: parentTitle.value ?? "Parent session",
    task: null,
    state: item ? sessionAgentState(item) : "idle",
    sessionId: link.parentId,
    work: null,
    session: item,
  };
});

const parentRunsOn = computed(() => {
  const row = parentRow.value;
  if (!row || !parent.value) return null;
  const item = parentSession.value;
  const project = item?.projectName ?? null;
  const harness = harnessName(item?.harnessType);
  const modelId = sessionModelId(item);
  const model = modelId ? modelDisplayName(modelId, models.value) : null;
  return [project, harness, model].filter(Boolean).join(" · ") || null;
});

function openParent(): void {
  const link = parent.value;
  if (!link) return;
  const item = parentSession.value;
  void router.navigate({
    to: "/sessions/$id",
    params: { id: link.parentId },
    search: { instanceId: item?.instanceId ?? undefined, parentSessionId: undefined },
  });
}

const summary = computed(() => {
  const running = agents.value.running.filter((row) => row.state === "running").length;
  const waiting = agents.value.running.filter((row) => row.state === "waiting").length;
  const parts: string[] = [];
  if (running > 0) parts.push(`${running} running`);
  if (waiting > 0) parts.push(`${waiting} waiting for you`);
  return parts.join(" · ");
});

const isEmpty = computed(() => !parentRow.value && agents.value.running.length + agents.value.started.length + agents.value.earlier.length === 0);
</script>

<template>
  <div
    class="agents-canvas"
    data-testid="agents-canvas"
  >
    <header class="agents-canvas__head">
      <h3 class="agents-canvas__title">
        Lineage
      </h3>
      <span
        v-if="summary"
        class="agents-canvas__summary"
        data-testid="agents-summary"
      >{{ summary }}</span>
    </header>

    <p
      v-if="isEmpty"
      class="agents-canvas__empty"
    >
      Subagents this session's agent starts, and sessions it forks or starts, show here.
    </p>

    <section
      v-if="parentRow"
      class="agents-section"
      aria-labelledby="agents-parent"
      data-testid="agents-parent"
    >
      <h4
        id="agents-parent"
        class="agents-section__head"
      >
        Parent
      </h4>
      <AgentsCanvasRow
        :row="parentRow"
        :now="work.now.value"
        :runs-on="parentRunsOn"
        :note="parent ? lineageParentNote(parent.kind) : null"
        parent
        @select="openParent"
      />
    </section>

    <section
      v-if="agents.running.length > 0"
      class="agents-section"
      aria-labelledby="agents-running"
      data-testid="agents-running"
    >
      <h4
        id="agents-running"
        class="agents-section__head"
      >
        Running now<span class="agents-section__count">{{ agents.running.length }}</span>
      </h4>
      <template
        v-for="row in agents.running"
        :key="row.key"
      >
        <AgentsCanvasRow
          :row="row"
          :now="work.now.value"
          :runs-on="runsOn(row)"
          :note="note(row)"
          :selected="shownKey === row.key"
          expandable
          @select="select(row)"
        />
        <AgentDetail
          v-if="shownKey === row.key"
          :row="row"
          :parent-session-id="sessionId"
          :stopping="row.work ? work.isStopping(row.work.id) : false"
          @model="rememberModel(row, $event)"
        />
      </template>
    </section>

    <section
      v-if="agents.started.length > 0"
      class="agents-section"
      aria-labelledby="agents-started"
      data-testid="agents-started"
    >
      <h4
        id="agents-started"
        class="agents-section__head"
      >
        Started by this session<span class="agents-section__count">{{ agents.started.length }}</span>
      </h4>
      <template
        v-for="row in agents.started"
        :key="row.key"
      >
        <AgentsCanvasRow
          :row="row"
          :now="work.now.value"
          :runs-on="runsOn(row)"
          :selected="shownKey === row.key"
          expandable
          @select="select(row)"
        />
        <AgentDetail
          v-if="shownKey === row.key"
          :row="row"
          :parent-session-id="sessionId"
          @model="rememberModel(row, $event)"
        />
      </template>
    </section>

    <section
      v-if="agents.earlier.length > 0"
      class="agents-section"
      data-testid="agents-earlier"
    >
      <button
        type="button"
        class="agents-section__fold"
        :aria-expanded="earlierOpen"
        data-testid="agents-earlier-toggle"
        @click="earlierOpen = !earlierOpen"
      >
        <ChevronDown
          class="agents-section__chevron"
          :class="{ 'agents-section__chevron--closed': !earlierOpen }"
          aria-hidden="true"
        />
        Earlier agents · {{ agents.earlier.length }}
      </button>
      <template v-if="earlierOpen">
        <template
          v-for="row in agents.earlier"
          :key="row.key"
        >
          <AgentsCanvasRow
            :row="row"
            :now="work.now.value"
            :runs-on="runsOn(row)"
            :selected="shownKey === row.key"
            expandable
            @select="select(row)"
          />
          <AgentDetail
            v-if="shownKey === row.key"
            :row="row"
            :parent-session-id="sessionId"
            @model="rememberModel(row, $event)"
          />
        </template>
      </template>
    </section>
  </div>
</template>

<style scoped>
.agents-canvas {
  display: flex;
  flex-direction: column;
  gap: 16px;
  height: 100%;
  overflow: auto;
  padding: 14px 14px 20px;
  font-size: 13px;
}

.agents-canvas__head {
  display: flex;
  align-items: baseline;
  gap: 8px;
}

.agents-canvas__title {
  margin: 0;
  font-size: 13px;
  font-weight: 600;
}

.agents-canvas__summary,
.agents-canvas__empty {
  color: var(--muted);
  font-size: 12px;
}

.agents-canvas__empty {
  margin: 0;
  line-height: 1.5;
}

.agents-section {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.agents-section__head {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 0;
  padding: 0 6px 4px;
  color: var(--muted);
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}

.agents-section__count {
  margin-left: auto;
  font-weight: 500;
  letter-spacing: 0;
  font-variant-numeric: tabular-nums;
}

.agents-section__fold {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--muted);
  font: inherit;
  font-size: 12.5px;
  text-align: left;
  cursor: pointer;
}

.agents-section__fold:hover {
  color: var(--text);
}

.agents-section__fold:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.agents-section__chevron {
  width: 14px;
  height: 14px;
  transition: transform var(--transition);
}

.agents-section__chevron--closed {
  transform: rotate(-90deg);
}
</style>
