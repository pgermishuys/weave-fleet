<script setup lang="ts">
import { computed, ref } from "vue";
import { ChevronRight, FileText } from "lucide-vue-next";
import ToolCard from "@/components/session/ToolCard.vue";
import ToolScreenshot from "@/components/session/ToolScreenshot.vue";
import ConversationPage from "@/components/session/ConversationPage.vue";
import BrowserSteps from "@/components/session/BrowserSteps.vue";
import AgentTaskRow from "@/components/session/AgentTaskRow.vue";
import type { ToolCardItem } from "@/components/session/activity-stream-tool-card";
import { getTool } from "@/lib/tools";
import { groupReads } from "@/lib/tool-row";
import StatusGlyph from "@/components/sessions/StatusGlyph.vue";
import type { VisualPayload } from "@/lib/visual-payload";

/** A message's tool calls: a row per call in one box, then the pages the calls showed, outside it. */
const props = defineProps<{
  tools?: ToolCardItem[];
  sessionId?: string;
}>();

const emit = defineEmits<{
  "expand-visual": [payload: VisualPayload];
  "show-canvas": [canvasId: string];
  /** Improve on a row that loaded one of Fleet's built-in skills. */
  "improve-skill": [skill: string, toolId: string];
}>();

/** The rows to draw: runs of completed reads become one "Read N files" row, which opens to the list. */
const entries = computed(() => groupReads(props.tools ?? []));
const openReads = ref<ReadonlySet<string>>(new Set());
function toggleReads(id: string): void {
  const next = new Set(openReads.value);
  if (!next.delete(id)) next.add(id);
  openReads.value = next;
}

const pagedTools = computed(() => (props.tools ?? []).filter((tool) => tool.page && !tool.delegation));

/** Calls that can take browser steps: OpenCode 2's Code Mode, and Fleet's own browser tools. */
function usesBrowser(kind: string | undefined): boolean {
  return getTool(kind).usesBrowser;
}

function handleExpandVisual(payload: VisualPayload): void {
  emit("expand-visual", payload);
}
</script>

<template>
  <div
    v-if="tools && tools.length > 0"
    class="msg-tools"
  >
    <template
      v-for="entry in entries"
      :key="entry.type === 'reads' ? entry.id : entry.tool.id"
    >
      <div
        v-if="entry.type === 'reads'"
        class="msg-reads"
        data-testid="tool-reads"
      >
        <button
          type="button"
          class="msg-reads__head"
          :aria-expanded="openReads.has(entry.id)"
          data-testid="tool-reads-toggle"
          @click="toggleReads(entry.id)"
        >
          <FileText
            class="msg-reads__icon"
            aria-hidden="true"
          />
          <span class="msg-reads__label">Read {{ entry.tools.length }} files</span>
          <StatusGlyph
            v-if="entry.tools.some((tool) => tool.status === 'Running' || tool.status === 'Pending')"
            class="msg-reads__running"
            status="running"
            data-testid="tool-reads-running"
          />
          <ChevronRight
            class="msg-reads__chevron"
            aria-hidden="true"
          />
        </button>
        <div
          v-if="openReads.has(entry.id)"
          class="msg-reads__list"
        >
          <ToolCard
            v-for="tool in entry.tools"
            :id="tool.id"
            :key="tool.id"
            :call-id="tool.callId"
            :session-id="sessionId"
            :input="tool.input"
            :title="tool.title"
            :kind="tool.kind"
            :status="tool.status"
            :summary="tool.summary"
            :output="tool.output"
            :initially-collapsed="true"
            :preview="tool.preview"
          />
        </div>
      </div>
      <template v-else>
        <AgentTaskRow
          v-if="entry.tool.delegation"
          :delegation="entry.tool.delegation"
        />
        <ToolCard
          v-else
          :id="entry.tool.id"
          :call-id="entry.tool.callId"
          :session-id="sessionId"
          :input="entry.tool.input"
          :title="entry.tool.title"
          :kind="entry.tool.kind"
          :status="entry.tool.status"
          :summary="entry.tool.summary"
          :output="entry.tool.output"
          :diff-lines="entry.tool.diffLines"
          :initially-collapsed="entry.tool.initiallyCollapsed"
          :preview="entry.tool.preview"
          :is-pattern-tool="entry.tool.isPatternTool"
          :canvas-id="entry.tool.canvasId"
          :improvable="entry.tool.improvable"
          :waiting="entry.tool.waiting"
          :duration-ms="entry.tool.durationMs"
          :exit="entry.tool.exit"
          @expand-visual="handleExpandVisual"
          @show-canvas="emit('show-canvas', $event)"
          @improve="emit('improve-skill', entry.tool.title, entry.tool.id)"
        />
        <ToolScreenshot
          v-if="entry.tool.screenshot && !entry.tool.delegation"
          :screenshot="entry.tool.screenshot"
          :title="entry.tool.title"
        />
        <BrowserSteps
          v-if="sessionId && !entry.tool.delegation && usesBrowser(entry.tool.kind)"
          :session-id="sessionId"
          :call-id="entry.tool.callId"
          :running="entry.tool.status === 'Running'"
        />
      </template>
    </template>
  </div>

  <!-- Outside the calls' list, on the conversation's own background: the page is part of the answer. -->
  <ConversationPage
    v-for="tool in pagedTools"
    :key="`page-${tool.id}`"
    :page="tool.page!"
    :title="tool.title"
  />
</template>

<style scoped>
/* Quiet rows: no box around the calls, the rows sit on the conversation's own background. */
.msg-tools {
  display: flex;
  flex-direction: column;
  margin: 2px 0;
}

.msg-reads__head {
  display: flex;
  align-items: center;
  gap: 9px;
  width: 100%;
  min-height: 30px;
  padding: 0 8px;
  border: 0;
  border-radius: calc(var(--radius-btn) - 2px);
  background: transparent;
  color: var(--muted);
  font: inherit;
  font-size: 13px;
  text-align: left;
  cursor: pointer;
  transition: background var(--transition);
}

.msg-reads__head:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
}

.msg-reads__head:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.msg-reads__icon {
  width: 14px;
  height: 14px;
  flex-shrink: 0;
  color: color-mix(in srgb, var(--muted) 75%, transparent);
}

.msg-reads__running {
  margin-left: auto;
  color: var(--running);
}

.msg-reads__chevron {
  width: 12px;
  height: 12px;
  transition: transform var(--transition);
}

.msg-reads__head[aria-expanded="true"] .msg-reads__chevron {
  transform: rotate(90deg);
}

.msg-reads__list {
  padding-left: 14px;
}
</style>
