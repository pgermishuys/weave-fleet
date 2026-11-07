<script setup lang="ts" generic="T extends string">
/** A setting's few choices side by side, one of them on: a radio group that reads as one control. */
defineProps<{
  options: readonly { value: T; label: string }[];
  value: T;
  label: string;
  /** `data-testid` of each choice is `<testid>-<value>`. */
  testid: string;
  disabled?: boolean;
}>();

const emit = defineEmits<{ pick: [value: T] }>();

function onKeydown(event: KeyboardEvent, options: readonly { value: T }[], index: number): void {
  const step = event.key === "ArrowRight" || event.key === "ArrowDown" ? 1 : event.key === "ArrowLeft" || event.key === "ArrowUp" ? -1 : 0;
  if (step === 0) return;
  event.preventDefault();
  const next = (index + step + options.length) % options.length;
  emit("pick", options[next].value);
  (event.currentTarget as HTMLElement).parentElement?.querySelectorAll<HTMLElement>("[role=radio]")[next]?.focus();
}
</script>

<template>
  <span
    class="segments"
    role="radiogroup"
    :aria-label="label"
  >
    <button
      v-for="(option, index) in options"
      :key="option.value"
      type="button"
      role="radio"
      class="segments__choice"
      :aria-checked="value === option.value"
      :tabindex="value === option.value ? 0 : -1"
      :disabled="disabled"
      :data-testid="`${testid}-${option.value}`"
      @click="value !== option.value && emit('pick', option.value)"
      @keydown="onKeydown($event, options, index)"
    >{{ option.label }}</button>
  </span>
</template>

<style scoped>
.segments {
  display: inline-flex;
  flex-shrink: 0;
  overflow: hidden;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--main-bg);
}

.segments__choice {
  padding: 4px 11px;
  color: var(--muted);
  font: inherit;
  font-size: 12.5px;
  white-space: nowrap;
  transition: background-color 120ms ease, color 120ms ease;
}

.segments__choice + .segments__choice {
  border-left: 1px solid var(--border);
}

.segments__choice:hover:not(:disabled) {
  color: var(--text);
}

.segments__choice[aria-checked="true"] {
  background: color-mix(in srgb, var(--accent) 18%, transparent);
  color: var(--text);
  font-weight: 600;
}

.segments__choice:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.segments__choice:disabled {
  cursor: progress;
}
</style>
