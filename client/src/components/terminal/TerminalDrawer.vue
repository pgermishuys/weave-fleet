<script setup lang="ts">
import { computed, nextTick, ref, watch } from "vue";
import { ChevronDown, Eraser, Plus, SquareTerminal, X } from "lucide-vue-next";
import TerminalView from "@/components/terminal/TerminalView.vue";
import { closeTerminalTab, openNewTerminal } from "@/composables/use-session-terminals";
import type { TerminalSummary } from "@/lib/terminal-api";
import { DEFAULT_DRAWER_HEIGHT, MIN_DRAWER_HEIGHT, useTerminalsStore } from "@/stores/terminals";

/**
 * The terminal drawer under the chat. It spans the conversation column, so the
 * right panel stays beside it. Shells run on the server and keep running when
 * the drawer is hidden; closing a tab ends its shell.
 */

const props = defineProps<{
  sessionId: string;
  /** The session's folder, where new shells start. */
  directory: string | null;
}>();

const MAX_HEIGHT_SHARE = 0.6;
const KEYBOARD_STEP = 24;
/** A size for a shell started before the drawer has measured itself; the socket resizes it straight after. */
const INITIAL_SIZE = { cols: 120, rows: 14 };

const store = useTerminalsStore();

const terminals = computed(() => store.terminalsFor(props.sessionId));
const active = computed(() => store.activeFor(props.sessionId));
const open = computed(() => store.isOpen(props.sessionId));

const drawerRef = ref<HTMLElement | null>(null);
const tablistRef = ref<HTMLElement | null>(null);
const views = ref<Record<string, InstanceType<typeof TerminalView> | null>>({});
const sizes = ref<Record<string, { cols: number; rows: number }>>({});
const focused = ref(false);
const creating = ref(false);
const error = ref<string | null>(null);
const dragging = ref(false);

// Views mount the first time their tab shows, and then stay: visiting a session
// never starts a shell by itself, and switching tabs doesn't reconnect.
const everOpened = ref(open.value);
const visited = ref<string[]>([]);

watch(
  [open, () => active.value?.id],
  ([isOpen, activeId]) => {
    if (!isOpen) return;
    everOpened.value = true;
    if (activeId && !visited.value.includes(activeId)) visited.value = [...visited.value, activeId];
  },
  { immediate: true },
);

const mounted = computed(() => terminals.value.filter((terminal) => visited.value.includes(terminal.id)));
const activeSize = computed(() => (active.value ? sizes.value[active.value.id] : undefined));

// Opening an empty drawer starts a shell, once the session's list has loaded.
watch(
  () => open.value && store.isLoaded(props.sessionId) && terminals.value.length === 0,
  (needsShell) => {
    if (needsShell && !creating.value && !error.value) void newTerminal();
  },
  { immediate: true },
);

watch(open, (isOpen) => {
  if (!isOpen) {
    error.value = null;
    if (focused.value) onViewFocus(false);
  }
});

async function newTerminal(): Promise<void> {
  if (creating.value) return;
  creating.value = true;
  error.value = null;
  const size = activeSize.value ?? INITIAL_SIZE;
  const failure = await openNewTerminal(props.sessionId, size.cols, size.rows);
  creating.value = false;
  error.value = failure;
}

function activate(terminal: TerminalSummary): void {
  store.setActive(props.sessionId, terminal.id);
}

function close(terminal: TerminalSummary): void {
  void closeTerminalTab(props.sessionId, terminal.id);
}

function onEnded(terminal: TerminalSummary): void {
  store.remove(props.sessionId, terminal.id);
  if (terminals.value.length === 0) store.setOpen(props.sessionId, false);
}

function hide(): void {
  store.setOpen(props.sessionId, false);
}

function clearActive(): void {
  if (active.value) views.value[active.value.id]?.clear();
}

function onViewFocus(value: boolean): void {
  focused.value = value;
  store.setFocused(value);
}

function onSize(terminal: TerminalSummary, cols: number, rows: number): void {
  sizes.value = { ...sizes.value, [terminal.id]: { cols, rows } };
}

function tabId(terminal: TerminalSummary): string {
  return `terminal-tab-${terminal.id.replace(/[^a-zA-Z0-9_-]/g, "-")}`;
}

function focusTab(terminal: TerminalSummary): void {
  void nextTick(() => tablistRef.value?.querySelector<HTMLElement>(`#${tabId(terminal)}`)?.focus());
}

function onTabKeydown(event: KeyboardEvent): void {
  const list = terminals.value;
  const index = list.findIndex((terminal) => terminal.id === active.value?.id);
  let next: TerminalSummary | undefined;

  if (event.key === "ArrowRight") next = list[(index + 1) % list.length];
  else if (event.key === "ArrowLeft") next = list[(index - 1 + list.length) % list.length];
  else if (event.key === "Home") next = list[0];
  else if (event.key === "End") next = list[list.length - 1];
  else if (event.key === "Delete" && active.value) {
    event.preventDefault();
    close(active.value);
    const after = store.activeFor(props.sessionId);
    if (after) focusTab(after);
    return;
  }

  if (!next) return;
  event.preventDefault();
  activate(next);
  focusTab(next);
}

// ── Resizing ────────────────────────────────────────────────────────────────

function maxHeight(): number {
  // Before layout (or without one) there's nothing to measure, so there's no cap yet.
  const available = drawerRef.value?.parentElement?.clientHeight ?? 0;
  if (available <= 0) return Number.MAX_SAFE_INTEGER;
  return Math.max(MIN_DRAWER_HEIGHT, Math.round(available * MAX_HEIGHT_SHARE));
}

const height = computed(() => Math.min(store.height, maxHeight()));

function onGripPointerDown(event: PointerEvent): void {
  if (event.button !== 0) return;
  const grip = event.currentTarget as HTMLElement;
  const startY = event.clientY;
  const startHeight = height.value;
  const limit = maxHeight();
  dragging.value = true;
  grip.setPointerCapture(event.pointerId);

  const move = (moveEvent: PointerEvent) => {
    store.setHeight(Math.min(limit, startHeight + (startY - moveEvent.clientY)));
  };
  const up = () => {
    dragging.value = false;
    grip.removeEventListener("pointermove", move);
    grip.removeEventListener("pointerup", up);
    grip.removeEventListener("pointercancel", up);
  };
  grip.addEventListener("pointermove", move);
  grip.addEventListener("pointerup", up);
  grip.addEventListener("pointercancel", up);
}

function onGripKeydown(event: KeyboardEvent): void {
  if (event.key !== "ArrowUp" && event.key !== "ArrowDown") return;
  event.preventDefault();
  const step = event.key === "ArrowUp" ? KEYBOARD_STEP : -KEYBOARD_STEP;
  store.setHeight(Math.min(maxHeight(), height.value + step));
}

function resetHeight(): void {
  store.setHeight(DEFAULT_DRAWER_HEIGHT);
}
</script>

<template>
  <section
    v-if="everOpened"
    v-show="open"
    ref="drawerRef"
    class="terminal-drawer"
    :class="{ 'terminal-drawer--focused': focused, 'terminal-drawer--dragging': dragging }"
    :style="{ height: `${height}px` }"
    aria-label="Terminal"
  >
    <div
      class="terminal-drawer__grip"
      role="separator"
      aria-orientation="horizontal"
      aria-label="Resize terminal"
      :aria-valuenow="height"
      :aria-valuemin="MIN_DRAWER_HEIGHT"
      :aria-valuemax="maxHeight()"
      tabindex="0"
      title="Drag to resize, double-click to reset"
      @pointerdown="onGripPointerDown"
      @dblclick="resetHeight"
      @keydown="onGripKeydown"
    />

    <header class="terminal-drawer__header">
      <div
        ref="tablistRef"
        class="terminal-tabs"
        role="tablist"
        aria-label="Terminals"
        @keydown="onTabKeydown"
      >
        <button
          v-for="terminal in terminals"
          :id="tabId(terminal)"
          :key="terminal.id"
          type="button"
          class="terminal-tab"
          :class="{ 'terminal-tab--active': terminal.id === active?.id }"
          role="tab"
          :aria-selected="terminal.id === active?.id"
          :tabindex="terminal.id === active?.id ? 0 : -1"
          @click="activate(terminal)"
          @auxclick.middle="close(terminal)"
        >
          <SquareTerminal
            :size="13"
            aria-hidden="true"
            class="terminal-tab__icon"
          />
          <span class="terminal-tab__label">{{ terminal.title }}</span>
          <span
            class="terminal-tab__close"
            role="button"
            :aria-label="`Close ${terminal.title}`"
            title="Close (ends the shell)"
            @click.stop="close(terminal)"
          >
            <X
              :size="11"
              aria-hidden="true"
            />
          </span>
        </button>
      </div>

      <button
        type="button"
        class="terminal-drawer__icon-btn"
        :disabled="creating"
        aria-label="New terminal"
        title="New terminal"
        @click="newTerminal"
      >
        <Plus
          :size="14"
          aria-hidden="true"
        />
      </button>

      <span class="terminal-drawer__spacer" />

      <span class="terminal-drawer__meta">
        <span
          v-if="props.directory"
          class="terminal-drawer__cwd"
          :title="props.directory"
        ><bdi>{{ props.directory }}</bdi></span>
        <span
          v-if="activeSize"
          class="terminal-drawer__size"
          :title="`${activeSize.cols} columns × ${activeSize.rows} rows`"
        >{{ activeSize.cols }}×{{ activeSize.rows }}</span>
      </span>

      <button
        type="button"
        class="terminal-drawer__icon-btn"
        :disabled="!active"
        aria-label="Clear terminal"
        title="Clear"
        @click="clearActive"
      >
        <Eraser
          :size="13"
          aria-hidden="true"
        />
      </button>
      <button
        type="button"
        class="terminal-drawer__icon-btn"
        aria-label="Hide terminal"
        title="Hide terminal (Ctrl J). Shells keep running."
        @click="hide"
      >
        <ChevronDown
          :size="15"
          aria-hidden="true"
        />
      </button>
    </header>

    <div class="terminal-drawer__body">
      <TerminalView
        v-for="terminal in mounted"
        v-show="terminal.id === active?.id"
        :key="terminal.id"
        :ref="(instance) => (views[terminal.id] = instance as InstanceType<typeof TerminalView> | null)"
        :session-id="props.sessionId"
        :terminal-id="terminal.id"
        :shown="open && terminal.id === active?.id"
        role="tabpanel"
        :aria-labelledby="tabId(terminal)"
        @size="(cols, rows) => onSize(terminal, cols, rows)"
        @focus="onViewFocus"
        @ended="onEnded(terminal)"
      />

      <div
        v-if="error"
        class="terminal-drawer__message"
        role="alert"
      >
        <p>{{ error }}</p>
        <button
          type="button"
          class="terminal-drawer__retry"
          @click="newTerminal"
        >
          Try again
        </button>
      </div>
      <p
        v-else-if="creating && terminals.length === 0"
        class="terminal-drawer__message terminal-drawer__message--quiet"
        role="status"
      >
        Starting a shell…
      </p>
    </div>
  </section>
</template>

<style scoped>
.terminal-drawer {
  --terminal-bg: color-mix(in srgb, var(--panel-bg) 96%, var(--text));

  position: relative;
  flex-shrink: 0;
  display: flex;
  flex-direction: column;
  min-height: 0;
  border-top: 1px solid var(--border);
  background: var(--terminal-bg);
  transition: border-color var(--transition);
}

.terminal-drawer--focused {
  border-top-color: color-mix(in srgb, var(--accent) 60%, transparent);
}

.terminal-drawer__grip {
  position: absolute;
  top: -5px;
  left: 0;
  right: 0;
  z-index: 2;
  height: 9px;
  display: grid;
  place-items: center;
  cursor: row-resize;
  touch-action: none;
}

.terminal-drawer__grip::after {
  content: "";
  width: 36px;
  height: 3px;
  border-radius: 2px;
  background: var(--muted);
  opacity: 0;
  transition: opacity var(--transition);
}

.terminal-drawer__grip:hover::after,
.terminal-drawer__grip:focus-visible::after,
.terminal-drawer--dragging .terminal-drawer__grip::after {
  opacity: 0.6;
}

.terminal-drawer__grip:focus-visible {
  outline: none;
}

.terminal-drawer__header {
  display: flex;
  align-items: center;
  gap: 2px;
  height: 36px;
  flex-shrink: 0;
  padding: 0 6px 0 8px;
  border-bottom: 1px solid var(--border);
  min-width: 0;
}

.terminal-tabs {
  display: flex;
  align-items: center;
  gap: 2px;
  min-width: 0;
  overflow-x: auto;
  scrollbar-width: none;
}

.terminal-tabs::-webkit-scrollbar {
  display: none;
}

.terminal-tab {
  display: inline-flex;
  flex-shrink: 0;
  align-items: center;
  gap: 6px;
  height: 26px;
  padding: 0 4px 0 8px;
  border: none;
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--muted);
  font-size: 12.5px;
  white-space: nowrap;
  cursor: pointer;
  transition: background-color var(--transition), color var(--transition);
}

.terminal-tab:hover:not(.terminal-tab--active) {
  background-color: color-mix(in srgb, var(--text) 5%, transparent);
  color: var(--text);
}

.terminal-tab--active {
  background-color: color-mix(in srgb, var(--text) 9%, transparent);
  color: var(--text);
  font-weight: 500;
}

.terminal-tab:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.terminal-tab__icon {
  flex-shrink: 0;
  opacity: 0.7;
}

.terminal-tab__label {
  max-width: 160px;
  overflow: hidden;
  text-overflow: ellipsis;
}

.terminal-tab__close {
  display: grid;
  place-items: center;
  width: 18px;
  height: 18px;
  border-radius: 4px;
  color: var(--muted);
  opacity: 0;
  transition: opacity var(--transition), background-color var(--transition);
}

.terminal-tab:hover .terminal-tab__close,
.terminal-tab--active .terminal-tab__close {
  opacity: 1;
}

.terminal-tab__close:hover {
  background-color: color-mix(in srgb, var(--text) 10%, transparent);
  color: var(--text);
}

.terminal-drawer__icon-btn {
  display: grid;
  flex-shrink: 0;
  place-items: center;
  width: 26px;
  height: 26px;
  border: none;
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--muted);
  cursor: pointer;
  transition: background-color var(--transition), color var(--transition);
}

.terminal-drawer__icon-btn:hover:not(:disabled) {
  background-color: color-mix(in srgb, var(--text) 6%, transparent);
  color: var(--text);
}

.terminal-drawer__icon-btn:disabled {
  opacity: 0.4;
  cursor: default;
}

.terminal-drawer__icon-btn:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.terminal-drawer__spacer {
  flex: 1;
  min-width: 8px;
}

.terminal-drawer__meta {
  display: flex;
  align-items: center;
  gap: 10px;
  min-width: 0;
  padding-right: 6px;
  font-family: var(--font-mono-stack);
  font-size: 11px;
  color: var(--muted);
  white-space: nowrap;
}

/* Long folders lose their start, not their end: the end is the part that tells them apart. */
.terminal-drawer__cwd {
  max-width: 260px;
  overflow: hidden;
  text-overflow: ellipsis;
  direction: rtl;
  text-align: left;
  opacity: 0.8;
}

.terminal-drawer__size {
  font-variant-numeric: tabular-nums;
  opacity: 0.8;
}

.terminal-drawer__body {
  position: relative;
  flex: 1;
  min-height: 0;
}

.terminal-drawer__message {
  position: absolute;
  inset: 0;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 10px;
  margin: 0;
  padding: 16px;
  color: var(--text);
  font-size: 12.5px;
  text-align: center;
}

.terminal-drawer__message p {
  margin: 0;
  max-width: 52ch;
}

.terminal-drawer__message--quiet {
  color: var(--muted);
}

.terminal-drawer__retry {
  height: 28px;
  padding: 0 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--panel-bg);
  color: var(--text);
  font-size: 12.5px;
  cursor: pointer;
}

.terminal-drawer__retry:hover {
  background: color-mix(in srgb, var(--text) 5%, var(--panel-bg));
}

.terminal-drawer__retry:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

@media (prefers-reduced-motion: reduce) {
  .terminal-drawer,
  .terminal-tab,
  .terminal-drawer__grip::after {
    transition: none;
  }
}
</style>
