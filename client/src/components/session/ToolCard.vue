<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import DiffView from "@/components/session/DiffView.vue";
import StatusGlyph from "@/components/sessions/StatusGlyph.vue";
import { useWorkspaceUiStore } from "@/stores/workspace-ui";
import { getToolIcon, getToolDisplayLabel } from "@/lib/tool-icons";
import { parseVisualPayload, type VisualPayload } from "@/lib/visual-payload";
import { getVisualRenderer } from "@/lib/visual-renderer-registry";

interface DiffLine {
  type: "add" | "remove" | "context";
  content: string;
  oldLineNumber?: number;
  newLineNumber?: number;
}

const props = withDefaults(
  defineProps<{
    id: string;
    title: string;
    kind?: string;
    status?: string;
    summary?: string;
    output?: string;
    diffLines?: DiffLine[];
    initiallyCollapsed?: boolean;
    preview?: string;
    isPatternTool?: boolean;
  }>(),
  {
    kind: "Tool",
    status: "Completed",
    summary: "",
    output: "",
    diffLines: () => [],
    initiallyCollapsed: false,
    preview: "",
    isPatternTool: false,
  },
);

const emit = defineEmits<{
  "expand-visual": [payload: VisualPayload];
}>();

const workspaceUiStore = useWorkspaceUiStore();

const shouldShowDiff = computed(() => workspaceUiStore.inlineToolDiffs && props.diffLines.length > 0);
const isCollapsed = shallowRef(props.initiallyCollapsed && !shouldShowDiff.value);

const toolIcon = computed(() => getToolIcon(props.kind));
const displayLabel = computed(() => getToolDisplayLabel(props.kind));

const visualPayload = computed(() => parseVisualPayload(props.output));
const visualRenderer = computed(() => {
  if (!visualPayload.value) return null;
  return getVisualRenderer(visualPayload.value.$type);
});

watch(
  () => props.initiallyCollapsed,
  (nextValue) => {
    if (shouldShowDiff.value) {
      isCollapsed.value = false;
      return;
    }

    isCollapsed.value = nextValue;
  },
);

watch(shouldShowDiff, (nextValue) => {
  if (nextValue) {
    isCollapsed.value = false;
  }
});

const TOOL_STATUS_TO_GLYPH: Record<string, string> = {
  Pending: "idle",
  Running: "resuming",
  Completed: "completed",
  Error: "error",
};

const glyphStatus = computed(() => TOOL_STATUS_TO_GLYPH[props.status] ?? "idle");

const STATUS_COLOR: Record<string, string> = {
  Pending: "var(--muted)",
  Running: "var(--running)",
  Completed: "var(--complete)",
  Error: "var(--error)",
};

const statusColor = computed(() => STATUS_COLOR[props.status] ?? "var(--muted)");

function handleToggle(event: Event): void {
  const target = event.target as HTMLDetailsElement;
  isCollapsed.value = !target.open;
}

function handleExpandVisual(): void {
  if (visualPayload.value) {
    emit("expand-visual", visualPayload.value);
  }
}
</script>

<template>
  <details
    class="tool-card"
    data-testid="tool-card"
    :data-tool-card-id="id"
    :open="!isCollapsed"
    @toggle="handleToggle"
  >
    <summary
      class="tool-header"
      data-testid="tool-card-header"
    >
      <component :is="toolIcon" class="tool-header__icon" />
      <span class="tool-header__label">{{ displayLabel }}</span>
      <span v-if="isPatternTool" class="tool-header__pattern">{{ title }}</span>
      <span v-else class="tool-header__detail">{{ title }}</span>
      <span
        v-if="status === 'Running' || status === 'Error'"
        class="tool-header__status"
        :style="{ color: statusColor }"
      >
        <StatusGlyph :status="glyphStatus" />
      </span>
    </summary>

    <p v-if="preview" class="tool-preview">{{ preview }}</p>

    <div
      :id="`${id}-body`"
      class="tool-body"
      data-testid="tool-card-body"
    >
      <p
        v-if="summary"
        class="tool-summary"
        data-testid="tool-card-summary"
      >
        {{ summary }}
      </p>

      <DiffView
        v-if="shouldShowDiff"
        :lines="diffLines"
      />

      <div
        v-if="visualPayload && visualRenderer"
        class="tool-visual"
        data-testid="tool-card-visual"
      >
        <component :is="visualRenderer" :content="visualPayload.content" />
        <button
          class="tool-visual__expand"
          data-testid="tool-visual-expand"
          @click="handleExpandVisual"
        >
          Expand
        </button>
      </div>

      <pre
        v-if="output && !visualPayload"
        class="tool-output"
        data-testid="tool-card-output"
      ><code>{{ output }}</code></pre>

      <p
        v-if="!summary && !output && !shouldShowDiff"
        class="tool-empty"
        data-testid="tool-card-empty-state"
      >
        No output captured
      </p>
    </div>
  </details>
</template>

<style scoped>
.tool-card {
  background: color-mix(in srgb, var(--panel-bg, #FAF9F7) 100%, transparent);
  border: 1px solid var(--border);
  border-radius: 0;
  margin-top: 8px;
  padding: 10px 12px;
}

.tool-header {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 13px;
  cursor: pointer;
  list-style: none;
  transition: color var(--transition);
}

.tool-header::-webkit-details-marker {
  display: none;
}

.tool-header::marker {
  display: none;
}

.tool-header__icon {
  width: 14px;
  height: 14px;
  color: var(--muted);
  flex-shrink: 0;
}

.tool-header__label {
  font-weight: 600;
  color: var(--text);
  font-family: var(--font-sans-stack);
  font-size: 13px;
  flex-shrink: 0;
}

.tool-header__detail {
  font-family: var(--font-mono-stack);
  font-size: 12px;
  color: var(--muted);
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  min-width: 0;
}

.tool-header__pattern {
  display: inline-block;
  padding: 2px 10px;
  background: color-mix(in srgb, var(--accent) 8%, transparent);
  border: 1px solid color-mix(in srgb, var(--accent) 25%, transparent);
  border-radius: 0;
  font-family: var(--font-mono-stack);
  font-size: 12px;
  font-weight: 500;
  color: var(--accent);
}

.tool-header__status {
  display: flex;
  align-items: center;
  font-size: 10px;
  margin-left: auto;
  flex-shrink: 0;
}

.tool-preview {
  margin: 6px 0 0;
  font-family: var(--font-mono-stack);
  font-size: 12px;
  color: var(--muted);
  line-height: 1.5;
}

.tool-body {
  margin-top: 4px;
}

.tool-summary {
  margin: 0 0 8px;
  color: var(--muted);
  font-size: 11px;
  line-height: 1.6;
}

.tool-output {
  margin: 0 0 8px;
  padding: 8px 10px;
  border: 1px solid var(--border);
  background: color-mix(in srgb, var(--panel-bg) 100%, transparent);
  color: var(--muted);
  font-family: var(--font-mono-stack);
  font-size: 10px;
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-word;
}

.tool-visual {
  margin: 8px 0;
  padding: 12px;
  border: 1px solid var(--border);
  background: color-mix(in srgb, var(--panel-bg) 100%, transparent);
  position: relative;
}

.tool-visual__expand {
  margin-top: 8px;
  padding: 4px 12px;
  background: var(--accent);
  color: white;
  border: none;
  border-radius: 0;
  font-family: var(--font-sans-stack);
  font-size: 11px;
  font-weight: 600;
  cursor: pointer;
  transition: opacity var(--transition);
}

.tool-visual__expand:hover {
  opacity: 0.85;
}
</style>
