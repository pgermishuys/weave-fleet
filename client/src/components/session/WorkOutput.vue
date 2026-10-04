<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, shallowRef, watch } from "vue";
import { readWorkOutput } from "@/composables/use-running-work";
import { isWorkRunning, type RunningWorkItem } from "@/lib/running-work";

defineOptions({
  name: "WorkOutput",
});

/**
 * The tail of a work item's output (a background shell's), read from Fleet a page at a time. While the work runs it
 * asks again every couple of seconds from where it left off, and keeps to the bottom unless you scrolled up.
 */
const props = defineProps<{
  item: RunningWorkItem;
}>();

/** How much of the output it keeps: the tail, as a terminal would show it. */
const TAIL_CHARS = 64 * 1024;
/** Where a long output starts reading, from its end, the first time. */
const TAIL_BYTES = 32 * 1024;
const REFRESH_MS = 2_000;
/** Pages read in one go before waiting for the next refresh. */
const MAX_PAGES = 8;

const text = shallowRef("");
const error = shallowRef<string | null>(null);
const loading = shallowRef(true);
/** Whether what's shown starts after the output's start: the harness dropped it, or it was skipped for the tail. */
const clipped = shallowRef(false);
const pre = ref<HTMLElement | null>(null);

let offset = 0;
let started = false;
let timer: ReturnType<typeof setTimeout> | null = null;
let disposed = false;

function append(chunk: string): void {
  if (!chunk) return;
  const next = text.value + chunk;
  if (next.length > TAIL_CHARS) {
    clipped.value = true;
    text.value = next.slice(next.length - TAIL_CHARS);
  } else {
    text.value = next;
  }
}

function atBottom(): boolean {
  const el = pre.value;
  return !el || el.scrollHeight - el.scrollTop - el.clientHeight < 24;
}

async function read(): Promise<void> {
  const follow = atBottom();
  for (let page = 0; page < MAX_PAGES && !disposed; page += 1) {
    const result = await readWorkOutput(props.item.sessionId, props.item.id, offset);
    if (disposed) return;
    if (!result.ok) {
      // Work that ended may have taken its output with it; what was read stays.
      error.value = text.value ? null : result.error;
      break;
    }

    error.value = null;
    const { output, nextOffset, size, truncated } = result.page;
    if (truncated) clipped.value = true;

    // A long output the first time: skip to its tail rather than reading it all.
    if (!started && size - nextOffset > TAIL_BYTES) {
      started = true;
      clipped.value = true;
      offset = size - TAIL_BYTES;
      continue;
    }

    if (!started && offset > 0) {
      // Started mid-way: drop the part line the tail starts in.
      const newline = output.indexOf("\n");
      append(newline === -1 ? output : output.slice(newline + 1));
    } else {
      append(output);
    }

    started = true;
    offset = Math.max(offset, nextOffset);
    if (nextOffset >= size || !output) break;
  }

  loading.value = false;
  if (follow) {
    await nextTick();
    if (pre.value) pre.value.scrollTop = pre.value.scrollHeight;
  }
}

function schedule(): void {
  if (disposed || timer !== null) return;
  timer = setTimeout(async () => {
    timer = null;
    await read();
    if (isWorkRunning(props.item)) schedule();
  }, REFRESH_MS);
}

void read().then(() => {
  if (isWorkRunning(props.item)) schedule();
});

// When it ends, one last read for whatever it wrote at the end.
watch(() => isWorkRunning(props.item), (running, wasRunning) => {
  if (!running && wasRunning) {
    if (timer !== null) clearTimeout(timer);
    timer = null;
    void read();
  }
});

onBeforeUnmount(() => {
  disposed = true;
  if (timer !== null) clearTimeout(timer);
});
</script>

<template>
  <div
    class="work-output"
    data-testid="work-output"
  >
    <p
      v-if="error"
      class="work-output__note"
      role="alert"
    >
      {{ error }}
    </p>
    <p
      v-else-if="loading && !text"
      class="work-output__note"
    >
      Reading the output…
    </p>
    <p
      v-else-if="!text"
      class="work-output__note"
    >
      No output yet.
    </p>
    <pre
      v-if="text"
      ref="pre"
      class="work-output__text"
      :aria-label="`Output of ${item.label ?? item.title}`"
      tabindex="0"
    ><span
      v-if="clipped"
      class="work-output__clipped"
    >… earlier output not shown
</span>{{ text }}</pre>
  </div>
</template>

<style scoped>
.work-output {
  margin: 2px 4px 4px 30px;
  min-width: 0;
}

.work-output__text {
  max-height: 220px;
  margin: 0;
  padding: 6px 8px;
  overflow: auto;
  border-radius: 6px;
  border: 1px solid var(--border);
  background: var(--panel-bg);
  color: var(--text);
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
  line-height: 1.45;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.work-output__clipped,
.work-output__note {
  color: var(--muted);
}

.work-output__note {
  margin: 0;
  padding: 4px 8px;
  font-size: 12px;
}
</style>
