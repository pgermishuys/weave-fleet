<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from "vue";
import { Terminal } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import { WebLinksAddon } from "@xterm/addon-web-links";
import "@xterm/xterm/css/xterm.css";
import { useResizeObserver } from "@vueuse/core";
import { storeToRefs } from "pinia";
import { openTerminalConnection } from "@/lib/terminal-connection";
import type { TerminalConnection, TerminalConnectionStatus } from "@/lib/terminal-socket";
import { currentTerminalFont, currentTerminalTheme } from "@/lib/terminal-theme";
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
}>();

const emit = defineEmits<{
  size: [cols: number, rows: number];
  focus: [focused: boolean];
  /** The shell ended or the terminal can't be reached; the tab should go. */
  ended: [];
}>();

const host = ref<HTMLElement | null>(null);
const status = ref<TerminalConnectionStatus>("connecting");
const { resolvedThemeId } = storeToRefs(useThemeStore());

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
  fitNow();

  connection = openTerminalConnection({
    sessionId: props.sessionId,
    terminalId: props.terminalId,
    cols: term.cols,
    rows: term.rows,
    handlers: {
      onReset: () => term?.reset(),
      onOutput: (data) => term?.write(data),
      onReady: () => {},
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
    if (!shown) return;
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
