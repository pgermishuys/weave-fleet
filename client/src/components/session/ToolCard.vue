<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import DiffView from "@/components/session/DiffView.vue";
import StatusGlyph from "@/components/sessions/StatusGlyph.vue";
import { useWorkspaceUiStore } from "@/stores/workspace-ui";

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
  }>(),
  {
    kind: "Tool",
    status: "Completed",
    summary: "",
    output: "",
    diffLines: () => [],
    initiallyCollapsed: false,
  },
);

const workspaceUiStore = useWorkspaceUiStore();

const shouldShowDiff = computed(() => workspaceUiStore.inlineToolDiffs && props.diffLines.length > 0);
const isCollapsed = shallowRef(props.initiallyCollapsed && !shouldShowDiff.value);
const shouldShowEmptyState = computed(() => !props.summary && !props.output && props.diffLines.length === 0);

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

const cardClassName = computed(() => ({
  collapsed: isCollapsed.value,
}));

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
</script>

<template>
  <details
    class="tool-card"
    :class="cardClassName"
    data-testid="tool-card"
    :data-tool-card-id="id"
    :open="!isCollapsed"
    @toggle="handleToggle"
  >
    <summary
      class="tool-header"
      data-testid="tool-card-header"
    >
      <div class="tool-header__meta">
        <span class="tool-header__kind">{{ kind }}</span>
        <span class="tool-header__title">{{ title }}</span>
      </div>
      <span class="tool-header__status" :style="{ color: statusColor }">
        <StatusGlyph :status="glyphStatus" />
      </span>
    </summary>

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

      <pre
        v-if="output"
        class="tool-output"
        data-testid="tool-card-output"
      ><code>{{ output }}</code></pre>

      <p
        v-if="shouldShowEmptyState"
        class="tool-empty-state"
        data-testid="tool-card-empty-state"
      >
        No output captured
      </p>
    </div>
  </details>
</template>

<style scoped>
.tool-card {
  background: var(--bg, #FAF9F7);
  border: 1px solid var(--border);
  padding: 10px 12px;
  margin-top: 8px;
  border-radius: 0;
}

.tool-header {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 4px 0;
  background: transparent;
  color: var(--muted);
  cursor: pointer;
  font-size: 12px;
  font-family: var(--font-mono-stack, 'Courier New', monospace);
  text-align: left;
  list-style: none;
  transition: color 0.15s ease;
}

.tool-header::-webkit-details-marker {
  display: none;
}

.tool-header::marker {
  display: none;
}

.tool-header:hover {
  color: var(--text);
}

.tool-header:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.tool-header__meta {
  display: flex;
  align-items: baseline;
  gap: 8px;
  min-width: 0;
  flex: 1;
}

.tool-header__kind {
  color: var(--muted);
  font-size: 10px;
  font-weight: 500;
  font-family: var(--font-mono-stack);
}

.tool-header__title {
  min-width: 0;
  overflow: hidden;
  color: var(--text);
  font-weight: 500;
  font-family: var(--font-mono-stack);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.tool-header__status {
  display: flex;
  align-items: center;
  font-size: 10px;
}

.tool-body {
  padding-left: 16px;
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
  background: var(--bg);
  color: var(--muted);
  font-family: var(--font-mono-stack);
  font-size: 10px;
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-word;
}

.tool-empty-state {
  margin: 0 0 8px;
  color: var(--muted);
  font-size: 11px;
  font-style: italic;
  line-height: 1.6;
}
</style>
