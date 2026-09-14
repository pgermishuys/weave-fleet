<script setup lang="ts">
/**
 * The message box the session composer and the New Session page share, so they can't drift: a
 * raised, rounded frame whose border shows focus, what goes above the toolbar (attachments, the
 * text area) in the default slot, and the toolbar row. Presentational only: listeners and classes
 * pass through to the frame.
 *
 * Give the text area `composer-frame__textarea` and the send button `composer-frame__send`.
 */

defineProps<{
  /** Something is being dragged over the frame. */
  dragging?: boolean;
}>();
</script>

<template>
  <div
    class="composer-frame"
    :class="{ 'composer-frame--dragging': dragging }"
  >
    <slot />
    <div class="composer-frame__toolbar">
      <slot name="toolbar" />
    </div>
  </div>
</template>

<style>
.composer-frame {
  position: relative;
  max-width: 760px;
  margin: 0 auto;
  border: 1px solid var(--border);
  border-radius: calc(var(--radius-panel) + 2px);
  background: var(--card-bg);
  box-shadow: 0 10px 28px -18px rgba(0, 0, 0, 0.5);
  transition: border-color var(--transition);
}

.composer-frame:focus-within {
  border-color: color-mix(in srgb, var(--accent) 55%, var(--border));
}

.composer-frame--dragging {
  border-color: var(--accent);
  background: color-mix(in srgb, var(--accent) 5%, var(--card-bg));
}

.composer-frame__textarea {
  width: 100%;
  min-height: 52px;
  max-height: 180px;
  padding: 14px 16px 6px;
  border: none;
  background: transparent;
  color: var(--text);
  resize: none;
  outline: none;
  font-size: 14px;
  line-height: 1.5;
}

.composer-frame__textarea::placeholder {
  color: var(--muted);
}

.composer-frame__toolbar {
  display: flex;
  align-items: center;
  gap: 2px;
  padding: 4px 8px 8px;
}

.composer-frame__send {
  width: 30px;
  height: 30px;
  margin-left: auto;
  padding: 0;
  border-radius: 999px;
}

/*
 * The frame's border already shows focus. The global focus ring is an !important rule in the
 * base layer, which no unlayered rule can beat, so drop it for the text area in-layer; otherwise
 * it draws a 1px line around the text area inside the rounded frame.
 */
@layer base {
  .composer-frame__textarea:focus-visible {
    border-color: transparent !important;
    box-shadow: none !important;
  }
}
</style>
