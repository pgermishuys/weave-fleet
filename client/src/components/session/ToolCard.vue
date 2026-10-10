<script setup lang="ts">
import { computed, shallowRef, watch, type Component } from "vue";
import { Check } from "lucide-vue-next";
import DiffView from "@/components/session/DiffView.vue";
import StatusGlyph from "@/components/sessions/StatusGlyph.vue";
import { useWorkspaceUiStore } from "@/stores/workspace-ui";
import { getToolIcon, getToolDisplayLabel } from "@/lib/tool-icons";
import { parseVisualPayload, type VisualPayload } from "@/lib/visual-payload";
import { getVisualRenderer } from "@/lib/visual-renderer-registry";
import ModTree from "@/components/mods/ModTree.vue";
import { toolRowViewId, toolRowViews } from "@/lib/mods/points";
import { useResolvedModView } from "@/lib/mods/resolve";
import type { ModAction } from "@/lib/mods/types";

interface DiffLine {
  type: "add" | "remove" | "context";
  content: string;
  oldLineNumber?: number;
  newLineNumber?: number;
}

const props = withDefaults(
  defineProps<{
    id: string;
    /** The call's id, which a mod's tree for this row is keyed by (with the session). */
    callId?: string;
    /** The session the call is in. Without it, no mod draws on the row. */
    sessionId?: string;
    title: string;
    kind?: string;
    status?: string;
    summary?: string;
    output?: string;
    diffLines?: DiffLine[];
    initiallyCollapsed?: boolean;
    preview?: string;
    isPatternTool?: boolean;
    /** A canvas the tool opened or changed: the card offers to show it. */
    canvasId?: string;
    /** A call that loaded one of Fleet's built-in skills: the card offers to improve the skill. */
    improvable?: boolean;
  }>(),
  {
    callId: undefined,
    sessionId: undefined,
    kind: "Tool",
    status: "Completed",
    summary: "",
    output: "",
    diffLines: () => [],
    initiallyCollapsed: false,
    preview: "",
    isPatternTool: false,
    canvasId: undefined,
    improvable: false,
  },
);

const emit = defineEmits<{
  "expand-visual": [payload: VisualPayload];
  "show-canvas": [canvasId: string];
  improve: [];
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
  Running: "running",
  // A call whose work OpenCode 2 moved into the background: the call is done, its work isn't.
  Background: "running",
  Completed: "completed",
  Cancelled: "idle",
  Stopped: "idle",
  Error: "error",
};

const glyphStatus = computed(() => TOOL_STATUS_TO_GLYPH[props.status] ?? "idle");

const STATUS_COLOR: Record<string, string> = {
  Pending: "var(--muted)",
  Running: "var(--running)",
  Background: "var(--running)",
  Completed: "var(--complete)",
  Cancelled: "var(--muted)",
  Stopped: "var(--muted)",
  Error: "var(--error)",
};

const statusColor = computed(() => STATUS_COLOR[props.status] ?? "var(--muted)");

// Line counts shown on the right of the row for tools that changed a file.
const diffStats = computed(() => {
  if (props.diffLines.length === 0) return null;
  let adds = 0;
  let removes = 0;
  for (const line of props.diffLines) {
    if (line.type === "add") adds++;
    else if (line.type === "remove") removes++;
  }
  return adds + removes > 0 ? { adds, removes } : null;
});

// A mod's tree on the line sits before Fleet's result, and its controls stay clear of the row's toggle.
// A mod can draw the row's line (ToolUse) and its opened body (ToolResult). Each is read for this row's id alone, so a
// contribution for another call doesn't redraw this one, and removal brings Fleet's own drawing back.
function viewId(site: "ToolUse" | "ToolResult"): string | undefined {
  return props.sessionId && props.callId ? toolRowViewId(site, props.sessionId, props.callId) : undefined;
}
const useView = useResolvedModView(toolRowViews, () => viewId("ToolUse"), "ToolUse");
const resultView = useResolvedModView(toolRowViews, () => viewId("ToolResult"), "ToolResult");

// Without a ToolResult tree the body is Fleet's own, drawn straight into `.tool-body` (no wrapper).
const FleetBody: Component = (_props, { slots }) => slots.fleet?.();
const resultBind = computed(() => {
  const view = resultView.value;
  if (!view.draws) return {};
  return {
    tree: view.tree,
    site: "ToolResult",
    sessionId: props.sessionId,
    onAction: (action: ModAction) => view.view.onAction?.(action, "desktop"),
  };
});

function handleToggle(event: Event): void {
  const target = event.target as HTMLDetailsElement;
  isCollapsed.value = !target.open;
}

function handleShowCanvas(): void {
  if (props.canvasId) emit("show-canvas", props.canvasId);
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
      :class="{ 'tool-header--mod': useView.draws }"
      data-testid="tool-card-header"
    >
      <component :is="toolIcon" class="tool-header__icon" />
      <span class="tool-header__label">{{ displayLabel }}</span>
      <span v-if="isPatternTool" class="tool-header__pattern">{{ title }}</span>
      <span v-else class="tool-header__detail">{{ title }}</span>
      <button
        v-if="canvasId && status === 'Completed'"
        type="button"
        class="tool-header__show"
        data-testid="tool-card-show"
        @click.prevent.stop="handleShowCanvas"
      >
        Show
      </button>
      <button
        v-if="improvable && status === 'Completed'"
        type="button"
        class="tool-header__show"
        data-testid="tool-card-improve"
        :aria-label="`Improve ${title}`"
        @click.prevent.stop="emit('improve')"
      >
        Improve
      </button>
      <span
        v-if="useView.draws"
        class="tool-header__mod"
        data-testid="tool-card-mod"
        @click.stop
      >
        <ModTree
          v-if="useView.tree"
          :tree="useView.tree"
          site="ToolUse"
          :session-id="sessionId"
          @action="useView.view.onAction?.($event, 'desktop')"
        />
      </span>
      <span
        v-if="status === 'Running' || status === 'Background' || status === 'Error'"
        class="tool-header__status"
        :style="{ color: statusColor }"
      >
        <span
          v-if="status === 'Background'"
          class="tool-header__background"
          data-testid="tool-card-background"
        >Background</span>
        <StatusGlyph :status="glyphStatus" />
      </span>
      <!-- Work the call started that Fleet stopped: said as the strip says it, not as a failure. -->
      <span
        v-else-if="status === 'Stopped'"
        class="tool-header__status tool-header__background"
        :style="{ color: statusColor }"
        data-testid="tool-card-stopped"
      >Stopped</span>
      <span
        v-else-if="diffStats"
        class="tool-header__result"
      >
        <span class="tool-header__adds">+{{ diffStats.adds }}</span>
        <span class="tool-header__removes">−{{ diffStats.removes }}</span>
      </span>
      <Check
        v-else-if="status === 'Completed' && !useView.draws"
        class="tool-header__done"
        aria-label="Completed"
      />
    </summary>

    <p v-if="preview && !useView.draws" class="tool-preview">{{ preview }}</p>

    <div
      :id="`${id}-body`"
      class="tool-body"
      data-testid="tool-card-body"
    >
      <component
        :is="resultView.draws ? ModTree : FleetBody"
        v-bind="resultBind"
      >
        <template #fleet>
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
        </template>
      </component>
    </div>
  </details>
</template>

<style scoped>
/* One quiet row per tool call; the message groups them in a single container. */
.tool-card {
  border-radius: calc(var(--radius-btn) - 2px);
  transition: background var(--transition);
}

.tool-card[open] {
  background: color-mix(in srgb, var(--text) 3%, transparent);
}

.tool-header {
  display: flex;
  align-items: center;
  gap: 9px;
  min-height: 30px;
  padding: 0 8px;
  border-radius: calc(var(--radius-btn) - 2px);
  font-size: 13px;
  cursor: pointer;
  list-style: none;
  transition: background var(--transition), color var(--transition);
}

.tool-header:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
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
  color: color-mix(in srgb, var(--muted) 75%, transparent);
  flex-shrink: 0;
}

.tool-header__label {
  font-weight: 500;
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
  padding: 1px 8px;
  background: color-mix(in srgb, var(--accent) 8%, transparent);
  border: 1px solid color-mix(in srgb, var(--accent) 25%, transparent);
  border-radius: calc(var(--radius-btn) - 2px);
  font-family: var(--font-mono-stack);
  font-size: 12px;
  font-weight: 500;
  color: var(--accent);
}

.tool-header__show {
  flex-shrink: 0;
  margin-left: auto;
  padding: 1px 8px;
  border-radius: calc(var(--radius-btn) - 2px);
  color: var(--accent);
  font-size: 12px;
  font-weight: 500;
  transition: background var(--transition);
}

.tool-header__show:hover {
  background: var(--accent-dim);
}

.tool-header__show:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

/* The mod's slot shrinks and wraps instead of widening the row (the title keeps truncating). Only the first
   right-aligned item takes the free space: after the Show button the slot sits beside it. */
.tool-header__mod {
  display: flex;
  flex: 0 1 auto;
  flex-wrap: wrap;
  align-items: center;
  justify-content: flex-end;
  gap: 4px 6px;
  min-width: 0;
  /* Never more than 60% of the row: the title keeps the rest (and at least its minimum, below). */
  max-width: 60%;
  margin-left: auto;
}

/* With a mod drawing on the line the command keeps a floor of 30% of the row (12 characters at most), ellipsizing
   inside it, so a long pill can't squeeze it to nothing. Rows no mod draws on keep today's CSS. */
.tool-header--mod .tool-header__detail,
.tool-header--mod .tool-header__pattern {
  min-width: min(30%, 12ch);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.tool-header__mod :deep(.mod-tree) {
  flex-wrap: wrap;
  min-width: 0;
  max-width: 100%;
}

.tool-header__show ~ .tool-header__mod,
.tool-header__mod ~ .tool-header__status,
.tool-header__mod ~ .tool-header__result {
  margin-left: 0;
}

.tool-header__show + .tool-header__done {
  margin-left: 0;
}

.tool-header__status,
.tool-header__result {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-left: auto;
  flex-shrink: 0;
}

.tool-header__background {
  font-size: 11px;
  font-weight: 500;
  letter-spacing: 0.02em;
  text-transform: uppercase;
}

.tool-header__result {
  font-family: var(--font-mono-stack);
  font-size: 12px;
  font-variant-numeric: tabular-nums;
}

.tool-header__adds {
  color: var(--running);
}

.tool-header__removes {
  color: var(--error);
}

.tool-header__done {
  width: 13px;
  height: 13px;
  margin-left: auto;
  flex-shrink: 0;
  color: var(--running);
}

.tool-preview {
  margin: 0;
  padding: 0 8px 6px 31px;
  font-family: var(--font-mono-stack);
  font-size: 12px;
  color: var(--muted);
  line-height: 1.5;
}

.tool-body {
  padding: 2px 8px 8px 31px;
}

.tool-summary {
  margin: 0 0 8px;
  color: var(--muted);
  font-size: 12px;
  line-height: 1.6;
}

.tool-output {
  margin: 0 0 4px;
  padding: 8px 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: color-mix(in srgb, var(--main-bg) 60%, transparent);
  color: var(--muted);
  font-family: var(--font-mono-stack);
  font-size: 12px;
  line-height: 1.55;
  white-space: pre-wrap;
  word-break: break-word;
}

.tool-visual {
  margin: 8px 0;
  padding: 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--panel-bg);
  position: relative;
}

.tool-visual__expand {
  margin-top: 8px;
  padding: 4px 12px;
  background: var(--accent);
  color: var(--primary-foreground);
  border: none;
  border-radius: var(--radius-btn);
  font-family: var(--font-sans-stack);
  font-size: 12px;
  font-weight: 600;
  cursor: pointer;
  transition: opacity var(--transition);
}

.tool-visual__expand:hover {
  opacity: 0.85;
}
</style>
