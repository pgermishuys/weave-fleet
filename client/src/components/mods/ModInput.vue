<script setup lang="ts">
import { inject, onBeforeUnmount, shallowRef, useId, watch } from "vue";
import { MOD_TREE } from "@/components/mods/mod-context";
import { str, type ModControlNode } from "@/components/mods/mod-style";
import { MOD_LIMITS } from "@/lib/mods/types";

/**
 * A text field. `value` is what it holds when drawn; typing replaces it until the tree's `value` changes. Enter or the
 * submit button sends `onSubmit`; changes send `onInput` at most `inputChangesPerSecond` a second: the first at once,
 * the ones inside the window coalesced into the latest, sent when the window ends.
 */
const props = defineProps<{ node: ModControlNode }>();

const WINDOW_MS = 1000 / MOD_LIMITS.inputChangesPerSecond;

const context = inject(MOD_TREE);
const id = useId();
const draft = shallowRef(str(props.node.props.value) ?? "");
let lastSentAt = -Infinity;
let pending: string | null = null;
let timer: ReturnType<typeof setTimeout> | undefined;

watch(() => str(props.node.props.value), (value) => {
  draft.value = value ?? "";
});

function sendInput(value: string): void {
  const handle = props.node.handles.onInput;
  if (!handle) return;
  lastSentAt = Date.now();
  context?.send({ handle, kind: "input", value });
}

function onInput(): void {
  if (!props.node.handles.onInput) return;
  const wait = lastSentAt + WINDOW_MS - Date.now();
  if (wait <= 0 && timer === undefined) {
    sendInput(draft.value);
    return;
  }
  pending = draft.value;
  timer ??= setTimeout(() => {
    timer = undefined;
    const value = pending;
    pending = null;
    if (value !== null) sendInput(value);
  }, Math.max(wait, 0));
}

function submit(): void {
  const handle = props.node.handles.onSubmit;
  if (handle) context?.send({ handle, kind: "submit", value: draft.value });
}

function onEnter(event: KeyboardEvent): void {
  if (!event.isComposing) submit();
}

// The field going away (a redraw, the pane closing) must not lose the last thing typed.
onBeforeUnmount(() => {
  clearTimeout(timer);
  timer = undefined;
  const value = pending;
  pending = null;
  if (value !== null) sendInput(value);
});
</script>

<template>
  <div class="mod-input">
    <label
      v-if="str(node.props.label)"
      class="mod-input__label"
      :for="id"
    >{{ str(node.props.label) }}</label>
    <div class="mod-input__row">
      <input
        :id="id"
        v-model="draft"
        class="mod-input__field"
        type="text"
        :placeholder="str(node.props.placeholder)"
        @input="onInput"
        @keydown.enter="onEnter"
      >
      <button
        v-if="node.handles.onSubmit"
        type="button"
        class="mod-input__submit"
        @click.stop.prevent="submit"
      >
        {{ str(node.props.submitLabel) ?? "Submit" }}
      </button>
    </div>
  </div>
</template>

<style scoped>
.mod-input {
  display: flex;
  flex: 1 1 0;
  flex-direction: column;
  gap: 4px;
  /* Room for a placeholder to read, but never wider than what holds it. */
  min-width: min(10em, 100%);
}

.mod-input__label {
  color: var(--muted);
  font-size: 12px;
}

.mod-input__row {
  display: flex;
  gap: 6px;
  min-width: 0;
}

.mod-input__field {
  flex: 1;
  min-width: 0;
  height: 30px;
  padding: 0 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
  color: var(--text);
  font: inherit;
  font-size: 12.5px;
  text-overflow: ellipsis;
}

.mod-input__field::placeholder {
  color: var(--muted);
  text-overflow: ellipsis;
}

.mod-input__field:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}

.mod-input__submit {
  flex: none;
  max-width: 50%;
  height: 30px;
  padding: 0 12px;
  border: 0;
  border-radius: var(--radius-btn);
  background: var(--accent);
  color: var(--primary-foreground);
  font: inherit;
  font-size: 12px;
  font-weight: 500;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  cursor: pointer;
}

.mod-input__submit:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}
</style>
