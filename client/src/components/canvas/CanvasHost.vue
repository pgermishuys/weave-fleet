<script setup lang="ts">
import { computed, inject, nextTick, onBeforeUnmount, ref, watch } from "vue";
import { Globe, Maximize2, Minimize2, Plus, X } from "lucide-vue-next";
import { useResizeObserver } from "@vueuse/core";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import BrowserOpenDialog from "@/components/canvas/BrowserOpenDialog.vue";
import type { UseDiffsResult } from "@/composables/use-diffs";
import { closeServerCanvas } from "@/composables/use-server-canvases";
import {
  CANVAS_TYPES,
  PICKABLE_CANVAS_KINDS,
  canvasIcon,
  canvasTitle,
  visualIcon,
  type CanvasTabBadge,
} from "@/lib/canvas-registry";
import type { VisualPayload } from "@/lib/visual-payload";
import {
  isCanvasClosable,
  useCanvasesStore,
  visualCanvasId,
  visualCanvasTitle,
  type CanvasInstance,
} from "@/stores/canvases";

const props = defineProps<{
  sessionId: string;
  tabBadges?: Partial<Record<string, CanvasTabBadge>>;
}>();

const store = useCanvasesStore();
const sharedDiffs = inject<UseDiffsResult | null>("sharedDiffs", null);

const state = computed(() => store.sessionCanvases(props.sessionId));
const canvases = computed(() => state.value.canvases);
const activeCanvas = computed<CanvasInstance>(
  () => canvases.value.find((canvas) => canvas.id === state.value.activeId) ?? canvases.value[0],
);
const changedCount = computed(() => sharedDiffs?.diffs.value.length ?? 0);
const openIds = computed(() => new Set(canvases.value.map((canvas) => canvas.id)));

// Visuals from the conversation, newest first, one entry per canvas title.
const conversationVisuals = computed(() => {
  const seen = new Set<string>();
  const result: VisualPayload[] = [];
  for (const payload of [...state.value.knownVisuals].reverse()) {
    const id = visualCanvasId(payload);
    if (seen.has(id)) continue;
    seen.add(id);
    result.push(payload);
  }
  return result;
});

function domId(canvas: CanvasInstance): string {
  return canvas.id.replace(/[^a-zA-Z0-9_-]/g, "-");
}

const tabId = (canvas: CanvasInstance) => `tab-${domId(canvas)}`;
const panelId = (canvas: CanvasInstance) => `panel-${domId(canvas)}`;

// New tabs slide in once; the class is dropped so re-renders don't replay it.
const enteringId = ref<string | null>(null);
let enterTimer: ReturnType<typeof setTimeout> | undefined;

watch(
  () => canvases.value.map((canvas) => canvas.id),
  (ids, previous) => {
    if (!previous) return;
    const added = ids.find((id) => !previous.includes(id));
    if (!added) return;
    enteringId.value = added;
    clearTimeout(enterTimer);
    enterTimer = setTimeout(() => {
      enteringId.value = null;
    }, 320);
  },
);

onBeforeUnmount(() => clearTimeout(enterTimer));

const tablistRef = ref<HTMLElement | null>(null);

// When tabs overflow, fade the edge that has more tabs behind it.
const fadeStart = ref(false);
const fadeEnd = ref(false);

function updateTabFades(): void {
  const el = tablistRef.value;
  if (!el) return;
  fadeStart.value = el.scrollLeft > 2;
  fadeEnd.value = el.scrollLeft + el.clientWidth < el.scrollWidth - 2;
}

useResizeObserver(tablistRef, updateTabFades);

// Keep the active tab in view, including ones the agent just opened.
watch(
  () => [activeCanvas.value.id, canvases.value.length] as const,
  () => {
    void nextTick(() => {
      const tab = tablistRef.value?.querySelector<HTMLElement>(`#${tabId(activeCanvas.value)}`);
      tab?.scrollIntoView?.({ block: "nearest", inline: "nearest" });
      updateTabFades();
    });
  },
  { immediate: true },
);

function focusTab(canvas: CanvasInstance): void {
  void nextTick(() => {
    tablistRef.value?.querySelector<HTMLElement>(`#${tabId(canvas)}`)?.focus();
  });
}

function activate(canvas: CanvasInstance): void {
  store.activate(props.sessionId, canvas.id);
}

function close(canvas: CanvasInstance): void {
  if (canvas.server) {
    void closeServerCanvas(props.sessionId, canvas.server.canvasId);
    return;
  }
  store.close(props.sessionId, canvas.id);
}

function onTabKeydown(event: KeyboardEvent): void {
  const list = canvases.value;
  const index = list.findIndex((canvas) => canvas.id === activeCanvas.value.id);
  let next: CanvasInstance | undefined;

  if (event.key === "ArrowRight") next = list[(index + 1) % list.length];
  else if (event.key === "ArrowLeft") next = list[(index - 1 + list.length) % list.length];
  else if (event.key === "Home") next = list[0];
  else if (event.key === "End") next = list[list.length - 1];
  else if (event.key === "Delete" && isCanvasClosable(activeCanvas.value)) {
    event.preventDefault();
    close(activeCanvas.value);
    const after = store.sessionCanvases(props.sessionId);
    const focused = after.canvases.find((canvas) => canvas.id === after.activeId);
    if (focused) focusTab(focused);
    return;
  }

  if (!next) return;
  event.preventDefault();
  activate(next);
  focusTab(next);
}

function openBuiltIn(kind: (typeof PICKABLE_CANVAS_KINDS)[number]): void {
  store.open(props.sessionId, kind);
}

function openVisual(payload: VisualPayload): void {
  store.openVisual(props.sessionId, payload);
}

const browserDialogOpen = ref(false);

// A tab whose page updated itself (a hot reload) pulses once. The class comes off and back on so a second
// update restarts the animation.
const pulsingIds = ref(new Set<string>());
const pulseTimers = new Map<string, ReturnType<typeof setTimeout>>();

function pulseTab(id: string): void {
  clearTimeout(pulseTimers.get(id));
  const without = new Set(pulsingIds.value);
  without.delete(id);
  pulsingIds.value = without;
  requestAnimationFrame(() => {
    pulsingIds.value = new Set(pulsingIds.value).add(id);
    pulseTimers.set(id, setTimeout(() => {
      const next = new Set(pulsingIds.value);
      next.delete(id);
      pulsingIds.value = next;
      pulseTimers.delete(id);
    }, 1400));
  });
}

watch(
  () => store.updatedAt,
  (next, previous) => {
    for (const [id, at] of Object.entries(next)) {
      if (previous?.[id] !== at && openIds.value.has(id)) pulseTab(id);
    }
  },
);

onBeforeUnmount(() => {
  for (const timer of pulseTimers.values()) clearTimeout(timer);
});

const activeProps = computed(() => {
  const canvas = activeCanvas.value;
  if (canvas.kind === "browser" && canvas.browser && canvas.server) {
    return {
      sessionId: props.sessionId,
      canvasId: canvas.server.canvasId,
      url: canvas.browser.url,
      appId: canvas.browser.appId,
    };
  }
  if (canvas.kind !== "visual" || !canvas.payload) return { sessionId: props.sessionId };
  return canvas.server ? { payload: canvas.payload, readonly: true } : { payload: canvas.payload };
});
</script>

<template>
  <div class="canvas-host">
    <div class="canvas-host__header">
      <div
        ref="tablistRef"
        class="canvas-tabs"
        :class="{ 'canvas-tabs--fade-start': fadeStart, 'canvas-tabs--fade-end': fadeEnd }"
        role="tablist"
        aria-label="Canvases"
        @keydown="onTabKeydown"
        @scroll="updateTabFades"
      >
        <button
          v-for="canvas in canvases"
          :id="tabId(canvas)"
          :key="canvas.id"
          type="button"
          class="canvas-tab"
          :class="{
            'canvas-tab--active': canvas.id === activeCanvas.id,
            'canvas-tab--entering': canvas.id === enteringId,
            'canvas-tab--updated': pulsingIds.has(canvas.id),
          }"
          role="tab"
          :aria-selected="canvas.id === activeCanvas.id"
          :aria-controls="panelId(canvas)"
          :tabindex="canvas.id === activeCanvas.id ? 0 : -1"
          :title="canvasTitle(canvas)"
          @click="activate(canvas)"
          @auxclick.middle="isCanvasClosable(canvas) && close(canvas)"
        >
          <component
            :is="canvasIcon(canvas)"
            :size="14"
            aria-hidden="true"
            class="canvas-tab__icon"
          />
          <span class="canvas-tab__label">{{ canvasTitle(canvas) }}</span>
          <span
            v-if="canvas.kind === 'changes' && changedCount > 0"
            class="canvas-tab__count"
          >{{ changedCount }}</span>
          <span
            v-else-if="props.tabBadges?.[canvas.id]?.attention"
            class="canvas-tab__alert"
            role="img"
            :aria-label="props.tabBadges[canvas.id]?.label ?? 'Needs attention'"
          />
          <span
            v-else-if="props.tabBadges?.[canvas.id]?.count"
            class="canvas-tab__count"
            :aria-label="props.tabBadges[canvas.id]?.label"
          >{{ props.tabBadges[canvas.id]?.count }}</span>
          <span
            v-if="isCanvasClosable(canvas)"
            class="canvas-tab__close"
            role="button"
            :aria-label="`Close ${canvasTitle(canvas)}`"
            @click.stop="close(canvas)"
          >
            <X
              :size="12"
              aria-hidden="true"
            />
          </span>
        </button>
      </div>

      <DropdownMenu>
        <DropdownMenuTrigger as-child>
          <button
            type="button"
            class="canvas-host__icon-btn"
            aria-label="Open a canvas"
            title="Open a canvas"
          >
            <Plus
              :size="15"
              aria-hidden="true"
            />
          </button>
        </DropdownMenuTrigger>
        <DropdownMenuContent
          align="start"
          class="canvas-picker w-64"
        >
          <DropdownMenuLabel class="canvas-picker__group">
            Built in
          </DropdownMenuLabel>
          <DropdownMenuItem
            v-for="kind in PICKABLE_CANVAS_KINDS"
            :key="kind"
            class="canvas-picker__item"
            @select="openBuiltIn(kind)"
          >
            <component
              :is="CANVAS_TYPES[kind].icon"
              aria-hidden="true"
            />
            <span>{{ CANVAS_TYPES[kind].label }}</span>
            <span
              v-if="openIds.has(kind)"
              class="canvas-picker__hint"
            >Open</span>
          </DropdownMenuItem>
          <DropdownMenuItem
            class="canvas-picker__item"
            data-testid="canvas-picker-browser"
            @select="browserDialogOpen = true"
          >
            <Globe aria-hidden="true" />
            <span>Browser…</span>
            <span class="canvas-picker__hint">Run or open a page</span>
          </DropdownMenuItem>
          <template v-if="conversationVisuals.length > 0">
            <DropdownMenuSeparator />
            <DropdownMenuLabel class="canvas-picker__group">
              From this conversation
            </DropdownMenuLabel>
            <DropdownMenuItem
              v-for="payload in conversationVisuals"
              :key="visualCanvasId(payload)"
              class="canvas-picker__item"
              @select="openVisual(payload)"
            >
              <component
                :is="visualIcon(payload)"
                aria-hidden="true"
              />
              <span class="canvas-picker__label">{{ visualCanvasTitle(payload) }}</span>
              <span
                v-if="openIds.has(visualCanvasId(payload))"
                class="canvas-picker__hint"
              >Open</span>
            </DropdownMenuItem>
          </template>
        </DropdownMenuContent>
      </DropdownMenu>

      <span class="canvas-host__spacer" />

      <button
        type="button"
        class="canvas-host__icon-btn"
        :aria-pressed="store.widened"
        :aria-label="store.widened ? 'Narrow canvas' : 'Widen canvas'"
        :title="store.widened ? 'Narrow canvas' : 'Widen canvas'"
        @click="store.toggleWidened()"
      >
        <Minimize2
          v-if="store.widened"
          :size="14"
          aria-hidden="true"
        />
        <Maximize2
          v-else
          :size="14"
          aria-hidden="true"
        />
      </button>
      <slot name="header-actions" />
    </div>

    <slot name="below-header" />

    <BrowserOpenDialog
      v-model:open="browserDialogOpen"
      :session-id="sessionId"
    />

    <div class="canvas-host__body">
      <KeepAlive :max="8">
        <component
          :is="CANVAS_TYPES[activeCanvas.kind].component"
          :id="panelId(activeCanvas)"
          :key="activeCanvas.id"
          v-bind="activeProps"
          role="tabpanel"
          :aria-labelledby="tabId(activeCanvas)"
        />
      </KeepAlive>
    </div>
  </div>
</template>

<style scoped>
.canvas-host {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.canvas-host__header {
  display: flex;
  align-items: center;
  gap: 2px;
  min-height: 56px;
  padding: 0 8px 0 10px;
  border-bottom: 1px solid var(--border);
}

.canvas-tabs {
  display: flex;
  align-items: center;
  gap: 2px;
  min-width: 0;
  overflow-x: auto;
  scrollbar-width: none;
}

.canvas-tabs::-webkit-scrollbar {
  display: none;
}

.canvas-tabs--fade-start {
  mask-image: linear-gradient(to right, transparent, #000 20px);
}

.canvas-tabs--fade-end {
  mask-image: linear-gradient(to left, transparent, #000 20px);
}

.canvas-tabs--fade-start.canvas-tabs--fade-end {
  mask-image: linear-gradient(to right, transparent, #000 20px, #000 calc(100% - 20px), transparent);
}

.canvas-tab {
  display: inline-flex;
  flex-shrink: 0;
  align-items: center;
  gap: 6px;
  max-width: 200px;
  height: 28px;
  padding: 0 9px;
  border: none;
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--muted);
  font-size: 13px;
  white-space: nowrap;
  cursor: pointer;
  transition: background-color var(--transition), color var(--transition);
}

.canvas-tab:hover:not(.canvas-tab--active) {
  background-color: color-mix(in srgb, var(--text) 5%, transparent);
  color: var(--text);
}

.canvas-tab--active {
  background-color: color-mix(in srgb, var(--text) 9%, transparent);
  color: var(--text);
  font-weight: 500;
}

.canvas-tab:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.canvas-tab__icon {
  flex-shrink: 0;
}

.canvas-tab__label {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
}

.canvas-tab__count {
  font-size: 12px;
  font-weight: 500;
  color: var(--muted);
  font-variant-numeric: tabular-nums;
}

.canvas-tab__alert {
  width: 7px;
  height: 7px;
  flex-shrink: 0;
  border-radius: 50%;
  background: var(--error);
}

.canvas-tab__close {
  display: grid;
  place-items: center;
  width: 16px;
  height: 16px;
  margin-right: -4px;
  border-radius: 4px;
  color: var(--muted);
  opacity: 0;
  transition: opacity 120ms ease-out, background-color var(--transition), color var(--transition);
}

.canvas-tab:hover .canvas-tab__close,
.canvas-tab--active .canvas-tab__close,
.canvas-tab:focus-visible .canvas-tab__close {
  opacity: 1;
}

.canvas-tab__close:hover {
  background-color: color-mix(in srgb, var(--text) 10%, transparent);
  color: var(--text);
}

.canvas-tab--entering {
  animation: canvas-tab-in 240ms cubic-bezier(0.2, 0.8, 0.2, 1) both;
}

.canvas-tab--updated {
  animation: canvas-tab-updated 1.2s ease-out;
}

.canvas-tab--updated .canvas-tab__icon {
  animation: canvas-tab-updated-icon 1.2s ease-out;
}

@keyframes canvas-tab-updated {
  from {
    background-color: color-mix(in srgb, var(--accent) 22%, transparent);
  }
}

@keyframes canvas-tab-updated-icon {
  from,
  40% {
    color: var(--accent);
  }
}

@keyframes canvas-tab-in {
  from {
    opacity: 0;
    transform: translateX(8px) scale(0.96);
  }
  to {
    opacity: 1;
    transform: none;
  }
}

.canvas-host__icon-btn {
  display: grid;
  flex-shrink: 0;
  place-items: center;
  width: 28px;
  height: 28px;
  padding: 0;
  border: none;
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--muted);
  cursor: pointer;
  transition: background-color var(--transition), color var(--transition);
}

.canvas-host__icon-btn:hover,
.canvas-host__icon-btn[data-state="open"],
.canvas-host__icon-btn[aria-pressed="true"] {
  background-color: color-mix(in srgb, var(--text) 6%, transparent);
  color: var(--text);
}

.canvas-host__icon-btn:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.canvas-host__spacer {
  flex: 1;
  min-width: 8px;
}

.canvas-host__body {
  position: relative;
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}

@media (prefers-reduced-motion: reduce) {
  .canvas-tab,
  .canvas-tab__close,
  .canvas-host__icon-btn {
    transition: none;
  }

  .canvas-tab--entering,
  .canvas-tab--updated,
  .canvas-tab--updated .canvas-tab__icon {
    animation: none;
  }
}
</style>

<!-- The picker renders in a portal, outside this component's scope. -->
<style>
.canvas-picker {
  border-radius: var(--radius-panel);
  padding: 6px;
}

.canvas-picker__group {
  padding: 8px 8px 4px;
  font-size: 11px;
  font-weight: 600;
  color: var(--muted);
}

.canvas-picker__item {
  height: 34px;
  border-radius: var(--radius-btn);
  font-size: 13px;
}

.canvas-picker__label {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.canvas-picker__hint {
  margin-left: auto;
  padding-left: 12px;
  font-size: 11.5px;
  color: var(--muted);
}
</style>
