<script setup lang="ts">
import { Link, MessageSquarePlus, Quote, X } from "lucide-vue-next";

/**
 * The bar docked under a file or a diff while lines are picked or selected: what they are, then Copy link, Quote
 * and Add to message. Its buttons keep the selection (mousedown is prevented), so they act on what's selected.
 */
defineProps<{
  /** The file the lines are in, when the bar can be about more than one; it gives way first on a narrow bar. */
  file?: string;
  /** "L18–27", or "Old L4" for lines only the old side has. */
  label: string;
  /** "10 lines". */
  count: string;
  /** What Copy link copies; no Copy link when null (old lines have no place in the file to link to). */
  link: string | null;
}>();

defineEmits<{
  "copy-link": [];
  quote: [];
  add: [];
  clear: [];
}>();
</script>

<template>
  <div
    class="lines-bar"
    role="toolbar"
    aria-label="Picked lines"
    data-testid="line-bar"
  >
    <span class="lines-bar__label">
      <span
        v-if="file"
        class="lines-bar__file"
        data-testid="line-bar-file"
        :title="file"
      >{{ file }}</span>
      <b data-testid="line-bar-label">{{ label }}</b>
      <span class="lines-bar__count">{{ count }}</span>
    </span>
    <span class="lines-bar__spacer" />
    <button
      v-if="link !== null"
      type="button"
      aria-label="Copy link"
      :title="`Copy ${link}`"
      data-testid="copy-link"
      @mousedown.prevent
      @click="$emit('copy-link')"
    >
      <Link
        :size="13"
        aria-hidden="true"
      />
      <span class="lines-bar__text">Copy link</span>
    </button>
    <button
      type="button"
      aria-label="Quote"
      title="Copy the lines as a quote"
      data-testid="copy-quote"
      @mousedown.prevent
      @click="$emit('quote')"
    >
      <Quote
        :size="13"
        aria-hidden="true"
      />
      <span class="lines-bar__text">Quote</span>
    </button>
    <button
      type="button"
      class="is-primary"
      data-testid="add-to-message"
      @mousedown.prevent
      @click="$emit('add')"
    >
      <MessageSquarePlus
        :size="13"
        aria-hidden="true"
      />
      Add to message
    </button>
    <button
      type="button"
      class="lines-bar__clear"
      aria-label="Clear"
      title="Clear (Esc)"
      data-testid="line-bar-clear"
      @mousedown.prevent
      @click="$emit('clear')"
    >
      <X
        :size="13"
        aria-hidden="true"
      />
    </button>
  </div>
</template>

<style scoped>
/* Docked, so scrolling the file never hides it. */
.lines-bar {
  display: flex;
  flex-shrink: 0;
  align-items: center;
  gap: 4px;
  margin: 0 8px 8px;
  padding: 5px 6px 5px 12px;
  border: 1px solid var(--border);
  border-radius: 10px;
  background: var(--card-bg);
  box-shadow: 0 16px 40px -12px rgba(0, 0, 0, 0.45);
  font-size: 12px;
  white-space: nowrap;
  container-type: inline-size;
  animation: lines-bar-pop 140ms ease-out;
}

.lines-bar__spacer {
  flex: 1;
}

/* A long file name gives way before the lines and the buttons do. */
.lines-bar__label {
  display: inline-flex;
  align-items: baseline;
  gap: 6px;
  min-width: 0;
  color: var(--muted);
  font-size: 11px;
}

.lines-bar__file {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  font-family: var(--font-mono-stack);
  font-size: 12px;
  color: var(--text);
}

.lines-bar__count {
  flex: none;
}

.lines-bar__label b {
  flex: none;
  font-family: var(--font-mono-stack);
  font-size: 12px;
  font-weight: 600;
  color: var(--text);
}

.lines-bar button {
  display: inline-flex;
  flex: none;
  align-items: center;
  gap: 5px;
  height: 26px;
  padding: 0 8px;
  border: 1px solid var(--border);
  border-radius: 7px;
  background: transparent;
  color: var(--text);
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
  transition: border-color var(--transition), background-color var(--transition);
}

.lines-bar button:hover {
  border-color: var(--accent);
}

.lines-bar button:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}

.lines-bar button.is-primary {
  border-color: var(--accent);
  background: var(--accent);
  color: var(--primary-foreground);
}

.lines-bar button.lines-bar__clear {
  padding: 0 6px;
  border-color: transparent;
  color: var(--muted);
}

.lines-bar button.lines-bar__clear:hover {
  color: var(--text);
}

@container (max-width: 404px) {
  .lines-bar__text,
  .lines-bar__file + b + .lines-bar__count {
    display: none;
  }

  .lines-bar button {
    padding: 0 6px;
  }
}

@keyframes lines-bar-pop {
  from {
    opacity: 0;
    transform: scale(0.96);
  }
}

@media (prefers-reduced-motion: reduce) {
  .lines-bar {
    animation: none;
  }

  .lines-bar button {
    transition: none;
  }
}
</style>
