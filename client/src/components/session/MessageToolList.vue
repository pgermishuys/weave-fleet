<script setup lang="ts">
import { computed } from "vue";
import ToolCard from "@/components/session/ToolCard.vue";
import ToolScreenshot from "@/components/session/ToolScreenshot.vue";
import ConversationPage from "@/components/session/ConversationPage.vue";
import BrowserSteps from "@/components/session/BrowserSteps.vue";
import AgentTaskRow from "@/components/session/AgentTaskRow.vue";
import type { ToolCardItem } from "@/components/session/activity-stream-tool-card";
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

const pagedTools = computed(() => (props.tools ?? []).filter((tool) => tool.page && !tool.delegation));

/** Calls that can take browser steps: OpenCode 2's Code Mode, and Fleet's own browser tools. */
function usesBrowser(kind: string | undefined): boolean {
  return kind === "execute" || kind === "fleet_browser_read" || kind === "fleet_browser_act";
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
      v-for="tool in tools"
      :key="tool.id"
    >
      <AgentTaskRow
        v-if="tool.delegation"
        :delegation="tool.delegation"
      />
      <ToolCard
        v-else
        :id="tool.id"
        :title="tool.title"
        :kind="tool.kind"
        :status="tool.status"
        :summary="tool.summary"
        :output="tool.output"
        :diff-lines="tool.diffLines"
        :initially-collapsed="tool.initiallyCollapsed"
        :preview="tool.preview"
        :is-pattern-tool="tool.isPatternTool"
        :canvas-id="tool.canvasId"
        :improvable="tool.improvable"
        @expand-visual="handleExpandVisual"
        @show-canvas="emit('show-canvas', $event)"
        @improve="emit('improve-skill', tool.title, tool.id)"
      />
      <ToolScreenshot
        v-if="tool.screenshot && !tool.delegation"
        :screenshot="tool.screenshot"
        :title="tool.title"
      />
      <BrowserSteps
        v-if="sessionId && !tool.delegation && usesBrowser(tool.kind)"
        :session-id="sessionId"
        :call-id="tool.callId"
        :running="tool.status === 'Running'"
      />
    </template>
  </div>

  <!-- Outside the calls' box, on the conversation's own background: the page is part of the answer. -->
  <ConversationPage
    v-for="tool in pagedTools"
    :key="`page-${tool.id}`"
    :page="tool.page!"
    :title="tool.title"
  />
</template>

<style scoped>
.msg-tools {
  display: flex;
  flex-direction: column;
  margin: 10px 0 4px;
  padding: 4px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: color-mix(in srgb, var(--text) 3%, transparent);
}
</style>
