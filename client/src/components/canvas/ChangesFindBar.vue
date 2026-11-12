<script setup lang="ts">
import { computed, useTemplateRef } from "vue";
import { ChevronDown, ChevronUp, X } from "lucide-vue-next";

/**
 * The find bar of Changes: the query, "n of m", previous and next, and close. It sits under the Changes header, the way
 * a file tab's find sits at the top of the editor. Esc is handled by the view around it.
 */
const props = defineProps<{
  query: string;
  total: number;
  /** The index (from 0) of the current match. */
  current: number;
  /** Files whose contents are still loading for the search. */
  searching: boolean;
  /** Collapsed files the search doesn't read (lockfiles, very large diffs) until they're opened. */
  unsearched: number;
}>();

const emit = defineEmits<{
  "update:query": [value: string];
  step: [direction: 1 | -1];
  close: [];
}>();

const field = useTemplateRef<HTMLInputElement>("field");
defineExpose({ field });

const count = computed(() => {
  if (props.query.trim() === "") return "";
  if (props.total === 0) return props.searching ? "Searching…" : "No matches";
  return `${props.current + 1} of ${props.total}`;
});

function onKeydown(event: KeyboardEvent): void {
  if (event.key === "Enter") {
    event.preventDefault();
    emit("step", event.shiftKey ? -1 : 1);
  }
}
</script>

<template>
  <div
    class="cfind"
    role="search"
    aria-label="Find in changes"
    data-testid="changes-find"
  >
    <input
      ref="field"
      class="cfind__field"
      type="search"
      autocomplete="off"
      spellcheck="false"
      placeholder="Find in changes"
      aria-label="Find in changes"
      data-testid="changes-find-input"
      :value="query"
      @input="emit('update:query', ($event.target as HTMLInputElement).value)"
      @keydown="onKeydown"
    >
    <span
      class="cfind__count"
      role="status"
      aria-live="polite"
      data-testid="changes-find-count"
    >{{ count }}</span>
    <button
      type="button"
      class="cfind__icon"
      aria-label="Previous match"
      title="Previous match (Shift+Enter)"
      :disabled="total === 0"
      data-testid="changes-find-prev"
      @click="emit('step', -1)"
    >
      <ChevronUp aria-hidden="true" />
    </button>
    <button
      type="button"
      class="cfind__icon"
      aria-label="Next match"
      title="Next match (Enter)"
      :disabled="total === 0"
      data-testid="changes-find-next"
      @click="emit('step', 1)"
    >
      <ChevronDown aria-hidden="true" />
    </button>
    <button
      type="button"
      class="cfind__icon"
      aria-label="Close find"
      title="Close (Esc)"
      data-testid="changes-find-close"
      @click="emit('close')"
    >
      <X aria-hidden="true" />
    </button>
    <span
      v-if="unsearched > 0"
      class="cfind__note"
      data-testid="changes-find-unsearched"
      title="Lockfiles and very large diffs start collapsed and are searched once you open them"
    >{{ unsearched }} collapsed {{ unsearched === 1 ? "file" : "files" }} not searched</span>
  </div>
</template>

<style scoped>
.cfind {
  flex: none;
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 4px;
  padding: 6px 10px 6px 14px;
  border-bottom: 1px solid var(--border);
  background: var(--card-bg);
  font-size: 12px;
}

.cfind__field {
  flex: 1 1 160px;
  min-width: 80px;
  max-width: 320px;
  height: 28px;
  padding: 0 8px;
  border: 1px solid var(--border);
  border-radius: 6px;
  background: var(--background);
  color: var(--text);
  font-size: 13px;
}

/* The bar has its own close button; the browser's clear button beside it would be a second ×. */
.cfind__field::-webkit-search-cancel-button {
  display: none;
}

.cfind__field:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -1px;
}

.cfind__count {
  min-width: 64px;
  padding: 0 4px;
  color: var(--idle);
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}

.cfind__icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  border-radius: 6px;
  color: var(--text);
}

.cfind__icon svg {
  width: 16px;
  height: 16px;
}

.cfind__icon:hover:not(:disabled) {
  background: color-mix(in srgb, var(--accent) 14%, transparent);
}

.cfind__icon:disabled {
  opacity: 0.4;
}

.cfind__note {
  margin-left: auto;
  color: var(--idle);
  white-space: nowrap;
}
</style>
