<script setup lang="ts">
import { computed } from "vue";
import { ChevronDown } from "lucide-vue-next";
import BackgroundWorkRow from "@/components/session/BackgroundWorkRow.vue";
import { useModels } from "@/composables/use-models";
import { usePersistedState } from "@/composables/use-persisted-state";
import { useRunningWork } from "@/composables/use-running-work";
import { modelDisplayName } from "@/lib/turns";
import type { RunningWorkItem } from "@/lib/running-work";
import { useSessionsStore } from "@/stores/sessions";

defineOptions({
  name: "BackgroundStrip",
});

/**
 * What the session's agent left running in the background, above the queued messages: one row per shell, subagent,
 * monitor or task, with its elapsed time and what can be done with it. Something that finished stays a little while
 * with its result, then drops off. The header collapses it to one line; hidden when nothing runs or just finished.
 */
const props = defineProps<{
  sessionId: string;
}>();

const sessionsStore = useSessionsStore();
const { visible, running, finished, now, isStopping } = useRunningWork(() => props.sessionId);
const { models } = useModels(() => props.sessionId);
const [collapsed, setCollapsed] = usePersistedState("weave:background-strip-collapsed", false);

const summary = computed(() => {
  const parts: string[] = [];
  if (running.value.length > 0) parts.push(`${running.value.length} running`);
  if (finished.value.length > 0) parts.push(`${finished.value.length} finished`);
  return parts.join(" · ");
});

/** A subagent's model: its session's choice, else whatever answered there last. Unknown until Fleet has said. */
function modelOf(item: RunningWorkItem): string | null {
  if (item.kind !== "subagent" || !item.childSessionId) return null;
  const child = sessionsStore.sessions.find((session) => session.session.id === item.childSessionId);
  const modelId = child?.selectedModel?.modelID ?? child?.lastAssistantModelId;
  return modelId ? modelDisplayName(modelId, models.value) : null;
}
</script>

<template>
  <section
    v-if="visible.length > 0"
    class="background-strip"
    aria-label="Running in the background"
    data-testid="background-strip"
  >
    <button
      type="button"
      class="background-strip__header"
      :aria-expanded="!collapsed"
      data-testid="background-strip-toggle"
      :title="collapsed ? 'Show what runs in the background' : 'Collapse to one line'"
      @click="setCollapsed(!collapsed)"
    >
      <span class="background-strip__label">Background</span>
      <span
        class="background-strip__summary"
        data-testid="background-strip-summary"
      >{{ summary }}</span>
      <ChevronDown
        class="background-strip__chevron"
        :class="{ 'background-strip__chevron--collapsed': collapsed }"
        aria-hidden="true"
      />
    </button>
    <ol
      v-if="!collapsed"
      class="background-strip__list"
    >
      <BackgroundWorkRow
        v-for="item in visible"
        :key="item.id"
        :item="item"
        :now="now"
        :model="modelOf(item)"
        :stopping="isStopping(item.id)"
      />
    </ol>
  </section>
</template>

<style scoped>
.background-strip {
  container: background-work / inline-size;
  max-width: 760px;
  margin: 0 auto 6px;
  overflow: hidden;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
}

.background-strip__header {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 6px 8px 6px 12px;
  border: 0;
  background: transparent;
  color: var(--text);
  font: inherit;
  font-size: 12px;
  text-align: left;
  cursor: pointer;
}

.background-strip__label {
  flex-shrink: 0;
  color: var(--muted);
  font-size: 11px;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.background-strip__summary {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.background-strip__chevron {
  flex-shrink: 0;
  width: 14px;
  height: 14px;
  color: var(--muted);
  transition: transform var(--transition);
}

.background-strip__chevron--collapsed {
  transform: rotate(-90deg);
}

.background-strip__list {
  display: flex;
  flex-direction: column;
  gap: 2px;
  margin: 0;
  padding: 0 4px 4px;
  list-style: none;
}
</style>
