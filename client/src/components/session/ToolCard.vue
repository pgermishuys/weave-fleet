<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { ChevronDown } from "lucide-vue-next";
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

function toggleCollapsed(): void {
  isCollapsed.value = !isCollapsed.value;
}
</script>

<template>
  <article
    class="tool-card"
    :class="cardClassName"
    data-testid="tool-card"
    :data-tool-card-id="id"
  >
    <button
      type="button"
      class="tool-header"
      :aria-expanded="!isCollapsed"
      :aria-controls="`${id}-body`"
      data-testid="tool-card-header"
      @click="toggleCollapsed"
    >
      <ChevronDown
        class="tool-header__chevron"
        :class="{ 'tool-header__chevron--collapsed': isCollapsed }"
      />
      <div class="tool-header__meta">
        <span class="tool-header__kind">{{ kind }}</span>
        <span class="tool-header__title">{{ title }}</span>
      </div>
      <span class="tool-header__status" :style="{ color: statusColor }">
        <StatusGlyph :status="glyphStatus" />
      </span>
    </button>

    <Transition name="collapse">
      <div
        v-if="!isCollapsed"
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
    </Transition>
  </article>
</template>

<style scoped>
.tool-card {
  margin-top: 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  overflow: hidden;
  background: var(--card-bg);
  transition: border-color 0.25s ease, background-color 0.25s ease;
}

.tool-header {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 8px 12px;
  border: 0;
  background: transparent;
  color: inherit;
  cursor: pointer;
  font-size: 11px;
  text-align: left;
  transition: background-color 0.25s ease;
}

.tool-header:hover {
  background: rgba(255, 255, 255, 0.03);
}

.tool-header:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.tool-header__chevron {
  width: 14px;
  height: 14px;
  color: var(--muted);
  transition: transform 0.25s ease;
}

.tool-header__chevron--collapsed {
  transform: rotate(-90deg);
}

.tool-header__meta {
  display: flex;
  align-items: baseline;
  gap: 8px;
  min-width: 0;
  flex: 1;
}

.tool-header__kind {
  color: var(--accent);
  font-size: 10px;
  font-weight: 600;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}

.tool-header__title {
  min-width: 0;
  overflow: hidden;
  color: var(--text);
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.tool-header__status {
  display: flex;
  align-items: center;
  font-size: 10px;
}

.tool-body {
  overflow: hidden auto;
}

.tool-summary {
  margin: 0;
  padding: 0 12px 10px;
  color: #d4d4d8;
  font-size: 11px;
  line-height: 1.6;
}

.tool-output {
  margin: 0;
  padding: 12px;
  border-top: 1px solid rgba(255, 255, 255, 0.04);
  background: rgba(255, 255, 255, 0.02);
  color: #d4d4d8;
  font-family: ui-monospace, SFMono-Regular, SFMono-Regular, Consolas, "Liberation Mono", Menlo, monospace;
  font-size: 10px;
  line-height: 1.6;
  white-space: pre-wrap;
  word-break: break-word;
}

.tool-empty-state {
  margin: 0;
  padding: 0 12px 12px;
  color: var(--muted);
  font-size: 11px;
  font-style: italic;
  line-height: 1.6;
}
</style>
