<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, useTemplateRef, watch } from "vue";
import { ChevronDown, ChevronUp, LoaderCircle, X } from "lucide-vue-next";
import type { FindScope } from "@/lib/conversation-find";

/**
 * The find bar of a conversation: the query, "n of m", previous and next, the scope chips, and "searching older…"
 * while history pages load. On a desktop it floats at the top right of the conversation; on a phone (`docked`) it sits
 * above the keyboard.
 */
const props = defineProps<{
  query: string;
  scope: FindScope;
  /** The matches found so far. */
  total: number;
  /** The index (from 0) of the current match. */
  current: number;
  /** Older history is still loading for the search. */
  searchingOlder: boolean;
  /** The last older page failed to load; the search waits for a retry. */
  olderFailed?: boolean;
  docked?: boolean;
}>();

const emit = defineEmits<{
  "update:query": [value: string];
  "update:scope": [value: FindScope];
  step: [direction: 1 | -1];
  close: [];
  "stop-older": [];
  "retry-older": [];
}>();

const field = useTemplateRef<HTMLInputElement>("field");
defineExpose({ field });

const count = computed(() => {
  if (props.query.trim() === "") return "";
  return props.total === 0 ? "No matches" : `${props.current + 1} of ${props.total}`;
});

function onKeydown(event: KeyboardEvent): void {
  if (event.key === "Enter") {
    event.preventDefault();
    emit("step", event.shiftKey ? -1 : 1);
  }
}

// On a phone the bar rides above the on-screen keyboard.
const lift = ref(0);
function trackKeyboard(): void {
  const viewport = window.visualViewport;
  lift.value = props.docked && viewport ? Math.max(0, window.innerHeight - viewport.height - viewport.offsetTop) : 0;
}
onMounted(() => {
  window.visualViewport?.addEventListener("resize", trackKeyboard);
  window.visualViewport?.addEventListener("scroll", trackKeyboard);
  trackKeyboard();
});
onBeforeUnmount(() => {
  window.visualViewport?.removeEventListener("resize", trackKeyboard);
  window.visualViewport?.removeEventListener("scroll", trackKeyboard);
});
watch(() => props.docked, trackKeyboard);
</script>

<template>
  <div
    class="find-bar"
    :class="{ 'find-bar--docked': docked }"
    :style="docked ? { bottom: `${lift}px` } : undefined"
    role="search"
    aria-label="Find in this conversation"
    data-testid="conversation-find"
    @keydown.esc.stop.prevent="emit('close')"
  >
    <div class="find-bar__row">
      <input
        ref="field"
        class="find-bar__field"
        type="search"
        enterkeyhint="search"
        autocomplete="off"
        spellcheck="false"
        placeholder="Find in this conversation"
        aria-label="Find in this conversation"
        data-testid="conversation-find-input"
        :value="query"
        @input="emit('update:query', ($event.target as HTMLInputElement).value)"
        @keydown="onKeydown"
      >
      <span
        class="find-bar__count"
        role="status"
        aria-live="polite"
        data-testid="conversation-find-count"
      >{{ count }}</span>
      <button
        type="button"
        class="find-bar__icon"
        aria-label="Previous match"
        :disabled="total === 0"
        data-testid="conversation-find-prev"
        @click="emit('step', -1)"
      >
        <ChevronUp aria-hidden="true" />
      </button>
      <button
        type="button"
        class="find-bar__icon"
        aria-label="Next match"
        :disabled="total === 0"
        data-testid="conversation-find-next"
        @click="emit('step', 1)"
      >
        <ChevronDown aria-hidden="true" />
      </button>
      <button
        type="button"
        class="find-bar__icon"
        aria-label="Close find"
        data-testid="conversation-find-close"
        @click="emit('close')"
      >
        <X aria-hidden="true" />
      </button>
    </div>
    <div class="find-bar__row find-bar__row--meta">
      <button
        type="button"
        class="find-bar__chip"
        :aria-pressed="scope === 'messages'"
        data-testid="conversation-find-scope-messages"
        @click="emit('update:scope', 'messages')"
      >
        Messages
      </button>
      <button
        type="button"
        class="find-bar__chip"
        :aria-pressed="scope === 'tools'"
        data-testid="conversation-find-scope-tools"
        @click="emit('update:scope', scope === 'tools' ? 'messages' : 'tools')"
      >
        + Tool output
      </button>
      <span
        v-if="searchingOlder"
        class="find-bar__older"
        data-testid="conversation-find-older"
      >
        <LoaderCircle
          class="find-bar__spinner"
          aria-hidden="true"
        />
        searching older…
        <button
          type="button"
          class="find-bar__stop"
          @click="emit('stop-older')"
        >Stop</button>
      </span>
      <span
        v-else-if="olderFailed"
        class="find-bar__older"
        role="alert"
        data-testid="conversation-find-older-failed"
      >
        couldn't load older messages ·
        <button
          type="button"
          class="find-bar__stop"
          @click="emit('retry-older')"
        >Retry</button>
      </span>
    </div>
  </div>
</template>

<style scoped>
.find-bar {
  position: absolute;
  top: 10px;
  right: 18px;
  z-index: 5;
  display: flex;
  flex-direction: column;
  gap: 6px;
  width: min(420px, calc(100% - 36px));
  padding: 8px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
  box-shadow: 0 6px 24px rgb(0 0 0 / 0.25);
  color: var(--text);
  font-size: 12px;
}

.find-bar--docked {
  position: fixed;
  /* Above the phone's composer bar, below its sheets. */
  z-index: 35;
  top: auto;
  right: 0;
  left: 0;
  width: auto;
  border-radius: 12px 12px 0 0;
  padding-bottom: calc(8px + env(safe-area-inset-bottom, 0px));
}

.find-bar__row {
  display: flex;
  align-items: center;
  gap: 4px;
}

.find-bar__row--meta {
  gap: 6px;
}

.find-bar__field {
  flex: 1;
  min-width: 0;
  height: 30px;
  padding: 0 8px;
  border: 1px solid var(--border);
  border-radius: 6px;
  background: var(--background);
  color: var(--text);
  /* 16px keeps iOS from zooming the page when the field takes focus. */
  font-size: 16px;
}

@media (min-width: 768px) {
  .find-bar__field {
    font-size: 13px;
  }
}

.find-bar__field:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -1px;
}

.find-bar__count {
  min-width: 56px;
  color: var(--idle);
  font-variant-numeric: tabular-nums;
  text-align: right;
  white-space: nowrap;
}

.find-bar__icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  border-radius: 6px;
  color: var(--text);
}

.find-bar--docked .find-bar__icon {
  width: 40px;
  height: 40px;
}

.find-bar__icon svg {
  width: 16px;
  height: 16px;
}

.find-bar__icon:hover:not(:disabled) {
  background: color-mix(in srgb, var(--accent) 14%, transparent);
}

.find-bar__icon:disabled {
  opacity: 0.4;
}

.find-bar__chip {
  padding: 2px 9px;
  border: 1px solid var(--border);
  border-radius: 999px;
  color: var(--idle);
}

.find-bar__chip[aria-pressed="true"] {
  border-color: var(--accent);
  background: color-mix(in srgb, var(--accent) 14%, transparent);
  color: var(--text);
}

.find-bar__older {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  margin-left: auto;
  color: var(--idle);
}

.find-bar__spinner {
  width: 12px;
  height: 12px;
  animation: find-bar-spin 1s linear infinite;
}

.find-bar__stop {
  color: var(--accent);
  text-decoration: underline;
}

@keyframes find-bar-spin {
  to {
    transform: rotate(360deg);
  }
}

@media (prefers-reduced-motion: reduce) {
  .find-bar__spinner {
    animation: none;
  }
}
</style>
