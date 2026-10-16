<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, shallowRef, useTemplateRef } from "vue";
import { Check, ChevronDown, ChevronUp, Copy } from "lucide-vue-next";
import { parseToolOutputCached } from "@/lib/tool-output";

/**
 * What a tool printed, as the open row shows it: colour from the terminal codes, only the last frame of a progress
 * line, and the last lines first, the end being where a failing test or a build error says what went wrong. "Show
 * earlier lines" opens a scroll box of at most MAX_EXPANDED_LINES; Copy always gives the whole output as plain text.
 */
const props = defineProps<{
  output: string;
}>();

/** Lines shown until the user asks for the rest, as the `!` block does. */
const COLLAPSED_LINES = 12;
/** Lines drawn at most when expanded; past this the box says so, and Copy still takes everything. */
const MAX_EXPANDED_LINES = 2000;

const expanded = shallowRef(false);
const copied = shallowRef(false);
let copiedTimer: ReturnType<typeof setTimeout> | undefined;
const box = useTemplateRef<HTMLPreElement>("box");

const parsed = computed(() => parseToolOutputCached(props.output));
const total = computed(() => parsed.value.lines.length);
const isLong = computed(() => total.value > COLLAPSED_LINES);
const shownCount = computed(() => {
  if (!isLong.value) return total.value;
  return expanded.value ? Math.min(total.value, MAX_EXPANDED_LINES) : COLLAPSED_LINES;
});
const hiddenLines = computed(() => total.value - shownCount.value);
const shownLines = computed(() => parsed.value.lines.slice(hiddenLines.value));
const isCapped = computed(() => expanded.value && total.value > MAX_EXPANDED_LINES);

async function showEarlier(): Promise<void> {
  expanded.value = true;
  // The box opens on the same last lines the row showed, so the eye doesn't lose its place.
  await nextTick();
  if (box.value) box.value.scrollTop = box.value.scrollHeight;
}

async function copy(): Promise<void> {
  try {
    await navigator.clipboard.writeText(parsed.value.plain);
  } catch {
    // Clipboard failures are non-fatal.
    return;
  }
  copied.value = true;
  clearTimeout(copiedTimer);
  copiedTimer = setTimeout(() => (copied.value = false), 1200);
}

onBeforeUnmount(() => clearTimeout(copiedTimer));

function count(n: number, noun: string): string {
  return `${n.toLocaleString("en-US")} ${noun}${n === 1 ? "" : "s"}`;
}
</script>

<template>
  <div
    class="tool-output-body"
    data-testid="tool-card-output-body"
  >
    <div class="tool-output-bar">
      <span
        class="tool-output-bar__count"
        data-testid="tool-card-output-count"
      >{{ count(total, "line") }}</span>
      <button
        type="button"
        class="tool-output-bar__copy"
        data-testid="tool-card-output-copy"
        :aria-label="copied ? 'Copied' : 'Copy output'"
        @click="copy"
      >
        <Check
          v-if="copied"
          class="tool-output-bar__icon"
          aria-hidden="true"
        />
        <Copy
          v-else
          class="tool-output-bar__icon"
          aria-hidden="true"
        />
        {{ copied ? "Copied" : "Copy" }}
      </button>
    </div>
    <button
      v-if="isLong && !expanded"
      type="button"
      class="tool-output-toggle"
      data-testid="tool-card-output-expand"
      @click="showEarlier"
    >
      <ChevronUp
        class="tool-output-toggle__icon"
        aria-hidden="true"
      />
      Show {{ count(hiddenLines, "earlier line") }}
    </button>
    <p
      v-if="isCapped"
      class="tool-output-note"
      data-testid="tool-card-output-capped"
    >
      Showing the last {{ count(shownCount, "line") }} of {{ total.toLocaleString("en-US") }}. Copy takes all of them.
    </p>
    <pre
      ref="box"
      class="tool-output"
      :class="{ 'tool-output--expanded': expanded }"
      data-testid="tool-card-output"
    ><code><span v-for="(line, index) in shownLines" :key="hiddenLines + index"><span v-for="(span, at) in line" :key="at" :class="span.className" :style="span.style">{{ span.text }}</span>{{ index < shownLines.length - 1 ? "\n" : "" }}</span></code></pre>
    <button
      v-if="isLong && expanded"
      type="button"
      class="tool-output-toggle"
      data-testid="tool-card-output-collapse"
      @click="expanded = false"
    >
      <ChevronDown
        class="tool-output-toggle__icon"
        aria-hidden="true"
      />
      Show only the last {{ COLLAPSED_LINES }} lines
    </button>
  </div>
</template>

<style scoped>
.tool-output-body {
  margin: 0 0 4px;
}

.tool-output-bar {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 0 0 5px;
  color: var(--muted);
  font-size: 12px;
}

.tool-output-bar__count {
  flex: 1;
  font-variant-numeric: tabular-nums;
}

.tool-output-bar__copy {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 1px 8px;
  border: 1px solid var(--border);
  border-radius: calc(var(--radius-btn) - 2px);
  background: transparent;
  color: var(--muted);
  font-size: 12px;
  cursor: pointer;
  transition: color var(--transition), border-color var(--transition);
}

.tool-output-bar__copy:hover {
  color: var(--text);
  border-color: color-mix(in srgb, var(--text) 25%, transparent);
}

.tool-output-bar__copy:focus-visible,
.tool-output-toggle:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.tool-output-bar__icon,
.tool-output-toggle__icon {
  width: 12px;
  height: 12px;
}

.tool-output-toggle {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  margin: 0 0 5px;
  padding: 0;
  border: none;
  background: transparent;
  color: var(--muted);
  font-size: 12px;
  cursor: pointer;
}

.tool-output-toggle:hover {
  color: var(--text);
}

.tool-output + .tool-output-toggle {
  margin: 5px 0 0;
}

.tool-output-note {
  margin: 0 0 5px;
  color: var(--muted);
  font-size: 12px;
}

.tool-output {
  margin: 0;
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
  tab-size: 4;
}

.tool-output--expanded {
  max-height: 340px;
  overflow: auto;
}

/* Terminal colours in Fleet's palette, so they follow every theme. Bright ones lean toward the text colour. */
.ansi-fg-black,
.ansi-fg-bright-black {
  color: color-mix(in srgb, var(--muted) 80%, transparent);
}
.ansi-fg-red { color: var(--error); }
.ansi-fg-green { color: var(--running); }
.ansi-fg-yellow { color: var(--idle); }
.ansi-fg-blue { color: var(--complete); }
.ansi-fg-magenta { color: var(--queued); }
.ansi-fg-cyan { color: color-mix(in srgb, var(--complete) 50%, var(--running)); }
.ansi-fg-white,
.ansi-fg-bright-white { color: var(--text); }
.ansi-fg-bright-red { color: color-mix(in srgb, var(--error) 75%, var(--text)); }
.ansi-fg-bright-green { color: color-mix(in srgb, var(--running) 75%, var(--text)); }
.ansi-fg-bright-yellow { color: color-mix(in srgb, var(--idle) 75%, var(--text)); }
.ansi-fg-bright-blue { color: color-mix(in srgb, var(--complete) 75%, var(--text)); }
.ansi-fg-bright-magenta { color: color-mix(in srgb, var(--queued) 75%, var(--text)); }
.ansi-fg-bright-cyan { color: color-mix(in srgb, color-mix(in srgb, var(--complete) 50%, var(--running)) 75%, var(--text)); }

.ansi-bg-black,
.ansi-bg-bright-black { background-color: color-mix(in srgb, var(--muted) 25%, transparent); }
.ansi-bg-red,
.ansi-bg-bright-red { background-color: color-mix(in srgb, var(--error) 22%, transparent); }
.ansi-bg-green,
.ansi-bg-bright-green { background-color: color-mix(in srgb, var(--running) 22%, transparent); }
.ansi-bg-yellow,
.ansi-bg-bright-yellow { background-color: color-mix(in srgb, var(--idle) 22%, transparent); }
.ansi-bg-blue,
.ansi-bg-bright-blue { background-color: color-mix(in srgb, var(--complete) 22%, transparent); }
.ansi-bg-magenta,
.ansi-bg-bright-magenta { background-color: color-mix(in srgb, var(--queued) 22%, transparent); }
.ansi-bg-cyan,
.ansi-bg-bright-cyan { background-color: color-mix(in srgb, color-mix(in srgb, var(--complete) 50%, var(--running)) 22%, transparent); }
.ansi-bg-white,
.ansi-bg-bright-white { background-color: color-mix(in srgb, var(--text) 18%, transparent); }

.ansi-bold { font-weight: 600; }
.ansi-dim { opacity: 0.6; }
.ansi-italic { font-style: italic; }
.ansi-underline { text-decoration: underline; }
</style>
