<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from "vue";
import { Terminal } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import { WebLinksAddon } from "@xterm/addon-web-links";
import "@xterm/xterm/css/xterm.css";
import { useResizeObserver } from "@vueuse/core";
import { Copy, MessageSquarePlus } from "lucide-vue-next";
import { storeToRefs } from "pinia";
import type { GlobalShortcut } from "@/lib/command-registry";
import { openTerminalConnection } from "@/lib/terminal-connection";
import { terminalKeyOwner } from "@/lib/terminal-keys";
import { terminalLineRange } from "@/lib/format-terminal-context";
import type { TerminalConnection, TerminalConnectionStatus } from "@/lib/terminal-socket";
import { currentTerminalFont, currentTerminalTheme } from "@/lib/terminal-theme";
import { useKeybindingsStore } from "@/stores/keybindings";
import { useThemeStore } from "@/stores/theme";

/**
 * One terminal tab: an xterm.js terminal sized to its box and connected to the
 * shell. It stays mounted while its session is open, so switching tabs or
 * hiding the drawer doesn't reconnect.
 */

const props = defineProps<{
  sessionId: string;
  terminalId: string;
  /** Showing on screen: its tab is active and the drawer is open. */
  shown: boolean;
  /** Where the terminal lives when it isn't a session's, e.g. the setup terminal. */
  basePath?: string;
  /** Typed into the shell once it's ready, without pressing Enter: the user reads it and runs it. */
  initialInput?: string;
}>();

const emit = defineEmits<{
  size: [cols: number, rows: number];
  focus: [focused: boolean];
  /** The shell ended or the terminal can't be reached; the tab should go. */
  ended: [];
  /** The user asked to add the selected lines to their message. */
  attach: [lines: SelectedLines];
}>();

export interface SelectedLines {
  from: number;
  to: number;
  text: string;
}

const host = ref<HTMLElement | null>(null);
const status = ref<TerminalConnectionStatus>("connecting");
/** The "Add lines to message" bubble, placed over the selection. */
const selectionPop = ref<{ top: number; left: number; below: boolean; lines: SelectedLines } | null>(null);
const { resolvedThemeId } = storeToRefs(useThemeStore());
const { bindings } = storeToRefs(useKeybindingsStore());

const isMac = typeof navigator !== "undefined" && /Mac|iPhone|iPad|iPod/.test(navigator.userAgent);

/** Fleet shortcuts that still work while the terminal has focus: show/hide the terminal and the right panel. */
function passThrough(): GlobalShortcut[] {
  return ["toggle-terminal", "toggle-right-panel"]
    .map((id) => bindings.value[id]?.globalShortcut)
    .filter((shortcut): shortcut is GlobalShortcut => Boolean(shortcut));
}

/**
 * Keys typed into the terminal stop at its box, so Fleet's own shortcuts
 * (Esc to interrupt, Ctrl K for the palette, …) never fire from inside it.
 * Only the pass-through shortcuts carry on to Fleet.
 */
function onHostKeydown(event: KeyboardEvent): void {
  if (terminalKeyOwner(event, isMac, passThrough()) !== "fleet") event.stopPropagation();
}

let term: Terminal | null = null;
let fit: FitAddon | null = null;
let connection: TerminalConnection | null = null;
let fitFrame = 0;

function fitNow(): void {
  if (!term || !fit || !host.value || host.value.clientWidth === 0 || host.value.clientHeight === 0) return;
  try {
    fit.fit();
  } catch {
    // Measuring can fail while fonts load; the next resize tries again.
  }
}

function scheduleFit(): void {
  cancelAnimationFrame(fitFrame);
  fitFrame = requestAnimationFrame(fitNow);
}

useResizeObserver(host, scheduleFit);

/**
 * The selected rows as whole lines, numbered from the top of the scrollback.
 * A row that continues a wrapped line is joined back onto it.
 */
function selectedLines(): SelectedLines | null {
  const range = term?.hasSelection() ? term.getSelectionPosition() : undefined;
  if (!term || !range) return null;

  // A selection that ends at the very start of a row doesn't include that row.
  const lastRow = range.end.x === 0 && range.end.y > range.start.y ? range.end.y - 1 : range.end.y;
  const buffer = term.buffer.active;
  let text = "";
  for (let y = range.start.y; y <= lastRow; y++) {
    const line = buffer.getLine(y);
    if (!line) continue;
    if (y > range.start.y && !line.isWrapped) text += "\n";
    text += line.translateToString(true);
  }
  text = text.replace(/\s+$/, "");
  return text.trim() ? { from: range.start.y + 1, to: lastRow + 1, text } : null;
}

function placeSelectionPop(): void {
  const lines = selectedLines();
  const range = term?.getSelectionPosition();
  if (!lines || !range || !term || !host.value) {
    selectionPop.value = null;
    return;
  }

  const screen = host.value.querySelector<HTMLElement>(".xterm-screen");
  const rowHeight = (screen?.clientHeight ?? host.value.clientHeight) / term.rows;
  const colWidth = (screen?.clientWidth ?? host.value.clientWidth) / term.cols;
  const firstVisible = term.buffer.active.viewportY;
  const startRow = range.start.y - firstVisible;
  const endRow = range.end.y - firstVisible;
  // Above the selection, unless that's off the top: then under it.
  const below = startRow * rowHeight < 34;
  const top = below ? Math.min(endRow + 1, term.rows) * rowHeight + 6 : startRow * rowHeight + 6;
  const left = Math.min(Math.max(range.end.x * colWidth, 130), host.value.clientWidth - 130);
  selectionPop.value = { top, left, below, lines };
}

function attachSelection(): void {
  if (!selectionPop.value) return;
  emit("attach", selectionPop.value.lines);
  term?.clearSelection();
  selectionPop.value = null;
}

function copySelection(): void {
  const selection = term?.getSelection();
  if (selection) void navigator.clipboard?.writeText(selection).catch(() => {});
  term?.clearSelection();
  selectionPop.value = null;
}

function onFocus(): void {
  emit("focus", true);
}

function onBlur(): void {
  emit("focus", false);
}

onMounted(async () => {
  if (!host.value) return;

  term = new Terminal({
    allowTransparency: true,
    cursorBlink: true,
    fontFamily: currentTerminalFont(),
    fontSize: 12,
    lineHeight: 1.2,
    scrollback: 5000,
    theme: currentTerminalTheme(),
  });
  fit = new FitAddon();
  term.loadAddon(fit);
  term.loadAddon(new WebLinksAddon((_event, uri) => window.open(uri, "_blank", "noopener,noreferrer")));

  // Measure with the real font, not its fallback.
  try {
    await document.fonts?.load(`12px ${currentTerminalFont()}`);
  } catch {
    // Carry on with whatever font is there.
  }
  if (!host.value) return;
  term.open(host.value);
  term.attachCustomKeyEventHandler((event) => {
    if (event.type !== "keydown") return true;
    switch (terminalKeyOwner(event, isMac, passThrough())) {
      case "fleet":
      case "paste":
        return false;
      case "copy": {
        const selection = term?.getSelection();
        if (selection) void navigator.clipboard?.writeText(selection).catch(() => {});
        event.preventDefault();
        return false;
      }
      default:
        return true;
    }
  });
  host.value.addEventListener("keydown", onHostKeydown);
  fitNow();

  let typedInitialInput = false;
  connection = openTerminalConnection({
    sessionId: props.sessionId,
    terminalId: props.terminalId,
    basePath: props.basePath,
    cols: term.cols,
    rows: term.rows,
    handlers: {
      onReset: () => term?.reset(),
      onOutput: (data) => term?.write(data),
      onReady: () => {
        // Once only: a reconnect is ready again, and the command may already have run.
        if (!props.initialInput || typedInitialInput) return;
        typedInitialInput = true;
        connection?.write(props.initialInput);
      },
      onCleared: () => term?.clear(),
      onExit: () => {},
      onStatus: (next) => {
        status.value = next;
        if (next === "ended") emit("ended");
      },
    },
  });

  term.onData((data) => connection?.write(data));
  term.onBinary((data) => connection?.write(Uint8Array.from(data, (ch) => ch.charCodeAt(0) & 0xff)));
  term.onSelectionChange(placeSelectionPop);
  term.onScroll(() => {
    if (selectionPop.value) placeSelectionPop();
  });
  term.onResize(({ cols, rows }) => {
    connection?.resize(cols, rows);
    emit("size", cols, rows);
  });
  term.textarea?.addEventListener("focus", onFocus);
  term.textarea?.addEventListener("blur", onBlur);
  emit("size", term.cols, term.rows);

  if (props.shown) term.focus();
});

watch(
  () => props.shown,
  (shown) => {
    if (!shown) {
      selectionPop.value = null;
      return;
    }
    void nextTick(() => {
      fitNow();
      term?.focus();
    });
  },
);

watch(resolvedThemeId, () => {
  // The theme's variables change on the next render.
  void nextTick(() => {
    if (term) term.options.theme = currentTerminalTheme();
  });
});

onBeforeUnmount(() => {
  cancelAnimationFrame(fitFrame);
  host.value?.removeEventListener("keydown", onHostKeydown);
  term?.textarea?.removeEventListener("focus", onFocus);
  term?.textarea?.removeEventListener("blur", onBlur);
  connection?.dispose();
  term?.dispose();
  connection = null;
  term = null;
});

defineExpose({
  focus: () => term?.focus(),
  /** Clears this terminal's screen and saved scrollback, for everyone watching it. */
  clear: () => {
    term?.clear();
    connection?.clear();
  },
});
</script>

<template>
  <div class="terminal-view">
    <div
      ref="host"
      class="terminal-view__host"
    />
    <div
      v-if="selectionPop"
      class="terminal-view__pop"
      :class="{ 'terminal-view__pop--below': selectionPop.below }"
      :style="{ top: `${selectionPop.top}px`, left: `${selectionPop.left}px` }"
      @mousedown.prevent
    >
      <button
        type="button"
        class="terminal-view__pop-btn terminal-view__pop-btn--primary"
        @click="attachSelection"
      >
        <MessageSquarePlus
          :size="13"
          aria-hidden="true"
        />
        Add {{ terminalLineRange(selectionPop.lines.from, selectionPop.lines.to) }} to message
      </button>
      <button
        type="button"
        class="terminal-view__pop-btn"
        @click="copySelection"
      >
        <Copy
          :size="13"
          aria-hidden="true"
        />
        Copy
      </button>
    </div>
    <p
      v-if="status === 'reconnecting'"
      class="terminal-view__notice"
      role="status"
    >
      Reconnecting…
    </p>
    <div
      v-else-if="status === 'failed'"
      class="terminal-view__failed"
      role="alert"
    >
      <p>This terminal can't be reached. Its shell may have ended while Fleet was away.</p>
      <button
        type="button"
        class="terminal-view__failed-btn"
        @click="emit('ended')"
      >
        Close tab
      </button>
    </div>
  </div>
</template>

<style scoped>
.terminal-view {
  position: absolute;
  inset: 0;
  padding: 6px 4px 4px 12px;
}

.terminal-view__host {
  width: 100%;
  height: 100%;
}

/* xterm paints nothing behind the text; the drawer's colour shows through. */
.terminal-view__host :deep(.xterm),
.terminal-view__host :deep(.xterm-viewport),
.terminal-view__host :deep(.xterm-screen) {
  background: transparent !important;
}

/* xterm 6 draws its own scrollbar (from VS Code): thinner, and without VS Code's shadow. */
.terminal-view__host :deep(.xterm) {
  --vscode-scrollbar-shadow: transparent;
}

.terminal-view__host :deep(.xterm-scrollable-element > .scrollbar.vertical),
.terminal-view__host :deep(.xterm-scrollable-element > .scrollbar.vertical > .slider) {
  width: 8px !important;
}

.terminal-view__host :deep(.xterm-scrollable-element > .scrollbar.vertical > .slider) {
  border-radius: 4px;
}

.terminal-view__pop {
  position: absolute;
  z-index: 3;
  display: flex;
  gap: 2px;
  padding: 3px;
  border: 1px solid var(--border);
  border-radius: calc(var(--radius-btn) + 1px);
  background: var(--card-bg);
  box-shadow: 0 10px 30px -12px rgba(0, 0, 0, 0.35), 0 1px 2px rgba(0, 0, 0, 0.08);
  transform: translate(-50%, calc(-100% - 4px));
  animation: terminal-pop-in var(--transition) both;
}

.terminal-view__pop--below {
  transform: translate(-50%, 0);
}

.terminal-view__pop-btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 26px;
  padding: 0 9px;
  border: none;
  border-radius: calc(var(--radius-btn) - 2px);
  background: transparent;
  color: var(--text);
  font-size: 12px;
  font-weight: 500;
  white-space: nowrap;
  cursor: pointer;
}

.terminal-view__pop-btn:hover {
  background: color-mix(in srgb, var(--text) 6%, transparent);
}

.terminal-view__pop-btn:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.terminal-view__pop-btn--primary {
  color: var(--accent);
}

@keyframes terminal-pop-in {
  from {
    opacity: 0;
  }
}

@media (prefers-reduced-motion: reduce) {
  .terminal-view__pop {
    animation: none;
  }
}

.terminal-view__notice {
  position: absolute;
  top: 8px;
  right: 12px;
  margin: 0;
  padding: 2px 8px;
  border-radius: 6px;
  background: color-mix(in srgb, var(--idle) 14%, transparent);
  color: var(--idle);
  font-size: 11.5px;
  font-weight: 500;
  pointer-events: none;
}

.terminal-view__failed {
  position: absolute;
  inset: 0;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 10px;
  padding: 16px;
  background: color-mix(in srgb, var(--panel-bg) 85%, transparent);
  color: var(--muted);
  font-size: 12.5px;
  text-align: center;
}

.terminal-view__failed p {
  margin: 0;
  max-width: 44ch;
}

.terminal-view__failed-btn {
  height: 28px;
  padding: 0 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--panel-bg);
  color: var(--text);
  font-size: 12.5px;
  cursor: pointer;
}

.terminal-view__failed-btn:hover {
  background: color-mix(in srgb, var(--text) 5%, var(--panel-bg));
}

.terminal-view__failed-btn:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}
</style>
