<script setup lang="ts">
import { computed, shallowRef, watch, type Component } from "vue";
import DiffView from "@/components/session/DiffView.vue";
import ToolOutputBody from "@/components/session/ToolOutputBody.vue";
import StatusGlyph from "@/components/sessions/StatusGlyph.vue";
import { useWorkspaceUiStore } from "@/stores/workspace-ui";
import { getToolIcon, getToolDisplayLabel } from "@/lib/tool-icons";
import { parseVisualPayload, type VisualPayload } from "@/lib/visual-payload";
import { getVisualRenderer } from "@/lib/visual-renderer-registry";
import ModTree from "@/components/mods/ModTree.vue";
import { toolRowViewId, toolRowViews } from "@/lib/mods/points";
import { useModToolRows } from "@/lib/mods/renders";
import { useResolvedModView } from "@/lib/mods/resolve";
import type { ModAction } from "@/lib/mods/types";
import { isRetiredCanvasTool } from "@/components/session/activity-stream-tool-card";
import { formatCallDuration, isReadCall, isShellRow, isWriteCall, shellHeader } from "@/lib/tool-row";

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
    /** What the harness passed the tool; a mod that hooks the row is sent it. */
    input?: Record<string, unknown>;
    title: string;
    kind?: string;
    status?: string;
    summary?: string;
    output?: string;
    diffLines?: DiffLine[];
    /** A whole-file write's real change (the session's diff): the row shows this instead of counting its lines; null hides the counts. */
    diffStat?: { additions: number; deletions: number } | null;
    initiallyCollapsed?: boolean;
    preview?: string;
    isPatternTool?: boolean;
    /** A canvas the tool opened or changed: the card offers to show it. */
    canvasId?: string;
    /** A call that loaded one of Fleet's built-in skills: the card offers to improve the skill. */
    improvable?: boolean;
    /** The call waits for the user (a page they pick on): the row is amber. */
    waiting?: boolean;
    /** How long the call ran, when the harness timed it. */
    durationMs?: number;
    /** A shell call's exit code, when the harness reports one. */
    exit?: number;
  }>(),
  {
    callId: undefined,
    sessionId: undefined,
    input: undefined,
    kind: "Tool",
    status: "Completed",
    summary: "",
    output: "",
    diffLines: () => [],
    diffStat: undefined,
    initiallyCollapsed: false,
    preview: "",
    isPatternTool: false,
    canvasId: undefined,
    improvable: false,
    waiting: false,
    durationMs: undefined,
    exit: undefined,
  },
);

const emit = defineEmits<{
  "expand-visual": [payload: VisualPayload];
  "show-canvas": [canvasId: string];
  improve: [];
}>();

const workspaceUiStore = useWorkspaceUiStore();

const isRead = computed(() => isReadCall(props.kind));
const isWrite = computed(() => isWriteCall(props.kind));
const isShell = computed(() => isShellRow(props.kind));
// A write row opens on what it wrote; other rows show a diff only when the user turned inline diffs on.
const shouldShowDiff = computed(() => props.diffLines.length > 0 && (isWrite.value || workspaceUiStore.inlineToolDiffs));
/** An opened write shows at most this many lines, then offers the rest. */
const DIFF_PREVIEW_LINES = 12;
const showAllDiff = shallowRef(false);
const shownDiffLines = computed(() => (showAllDiff.value ? props.diffLines : props.diffLines.slice(0, DIFF_PREVIEW_LINES)));
const hiddenDiffLines = computed(() => props.diffLines.length - shownDiffLines.value.length);
const shellLabel = computed(() => shellHeader(props.input, props.title, props.exit, props.durationMs));
const tookLabel = computed(() => formatCallDuration(props.durationMs));
const isCollapsed = shallowRef(props.initiallyCollapsed && (isWrite.value || !shouldShowDiff.value));

const toolIcon = computed(() => getToolIcon(props.kind));
/** Copilot's turn row and HydraFusion's pass rows are a sentence of facts (model, time, credits), not a path: they wrap rather than clip. */
const wrapsDetail = computed(() => props.kind === "copilot_turn" || props.kind.startsWith("hydrafusion_"));
const displayLabel = computed(() => getToolDisplayLabel(props.kind));
const retired = computed(() => isRetiredCanvasTool(props.kind));

const visualPayload = computed(() => parseVisualPayload(props.output));
const visualRenderer = computed(() => {
  if (!visualPayload.value) return null;
  return getVisualRenderer(visualPayload.value.$type);
});

watch(
  () => props.initiallyCollapsed,
  (nextValue) => {
    if (shouldShowDiff.value && !isWrite.value) {
      isCollapsed.value = false;
      return;
    }

    isCollapsed.value = nextValue;
  },
);

watch(shouldShowDiff, (nextValue) => {
  if (nextValue && !isWrite.value) {
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

const glyphStatus = computed(() => (props.waiting ? "waiting_input" : TOOL_STATUS_TO_GLYPH[props.status] ?? "idle"));

const STATUS_COLOR: Record<string, string> = {
  Pending: "var(--muted)",
  Running: "var(--running)",
  Background: "var(--running)",
  Completed: "var(--complete)",
  Cancelled: "var(--muted)",
  Stopped: "var(--muted)",
  Error: "var(--error)",
};

const statusColor = computed(() => (props.waiting ? "var(--status-waiting)" : STATUS_COLOR[props.status] ?? "var(--muted)"));

// Line counts shown on the right of the row for tools that changed a file.
const diffStats = computed(() => {
  if (props.diffStat !== undefined) {
    return props.diffStat && props.diffStat.additions + props.diffStat.deletions > 0
      ? { adds: props.diffStat.additions, removes: props.diffStat.deletions }
      : null;
  }
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
// Ask the server for the trees of mods that hook this row. The status decides when; the output is read only to send.
useModToolRows(() => [
  {
    sessionId: props.sessionId,
    callId: props.callId,
    rawTool: props.kind,
    status: props.status,
    read: () => ({ input: props.input, output: props.output, title: props.title }),
  },
]);

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
    :class="{ 'tool-card--read': isRead, 'tool-card--write': isWrite, 'tool-card--shell': isShell }"
    data-testid="tool-card"
    :data-tool-card-id="id"
    :open="!isCollapsed"
    @toggle="handleToggle"
  >
    <summary
      class="tool-header"
      :class="{ 'tool-header--mod': useView.draws, 'tool-header--wraps': wrapsDetail }"
      data-testid="tool-card-header"
    >
      <component :is="toolIcon" class="tool-header__icon" />
      <span class="tool-header__label">{{ displayLabel }}</span>
      <span v-if="isPatternTool" class="tool-header__pattern">{{ title }}</span>
      <!-- A call with no path yet is titled with its own name, which the verb already says. -->
      <span v-else-if="title.toLowerCase() !== kind.toLowerCase()" class="tool-header__detail">{{ title }}</span>
      <span v-else class="tool-header__detail" />
      <span
        v-if="retired && status === 'Completed'"
        class="tool-header__retired"
        data-testid="tool-card-retired"
      >
        Diagram retired
      </span>
      <button
        v-else-if="canvasId && status === 'Completed'"
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
      <span
        v-else-if="isShell && tookLabel"
        class="tool-header__result tool-header__took"
        data-testid="tool-card-took"
      >{{ tookLabel }}</span>
    </summary>

    <!-- Quiet rows say nothing under the line while closed; a failed call keeps what it printed. -->
    <p v-if="preview && status === 'Error' && !useView.draws" class="tool-preview">{{ preview }}</p>

    <!-- A closed row draws no body: a long conversation holds many rows, and most stay closed. -->
    <div
      v-if="!isCollapsed"
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
            :lines="isWrite ? shownDiffLines : diffLines"
          />
          <button
            v-if="shouldShowDiff && isWrite && hiddenDiffLines > 0"
            type="button"
            class="tool-diff-more"
            data-testid="tool-card-diff-more"
            @click="showAllDiff = true"
          >
            Show all {{ diffLines.length }} lines
          </button>

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

          <ToolOutputBody
            v-if="output && !visualPayload && !(isWrite && shouldShowDiff)"
            :output="output"
            :header="isShell ? shellLabel : undefined"
          />

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

/* Reads are background work: the verb is as quiet as the path. */
.tool-card--read .tool-header__label {
  color: var(--muted);
  font-weight: 400;
}

.tool-diff-more {
  margin: 4px 0 0;
  padding: 0;
  border: 0;
  background: transparent;
  color: var(--muted);
  font-size: 12px;
  cursor: pointer;
}

.tool-diff-more:hover {
  color: var(--text);
}

.tool-header__took {
  color: var(--muted);
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

.tool-header--wraps {
  align-items: flex-start;
  padding-top: 6px;
  padding-bottom: 6px;
}

.tool-header--wraps .tool-header__detail {
  overflow: visible;
  white-space: normal;
  overflow-wrap: anywhere;
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

/* Where Show was: the diagram the call drew is gone. */
.tool-header__retired {
  flex-shrink: 0;
  margin-left: auto;
  padding: 1px 8px;
  color: var(--muted);
  font-size: 12px;
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
