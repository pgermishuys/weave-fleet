<script setup lang="ts">
import { computed, inject } from "vue";
import { modIcon } from "@/components/mods/mod-icons";
import { MOD_TREE } from "@/components/mods/mod-context";
import { flag, str, type ModControlNode } from "@/components/mods/mod-style";

/**
 * A button as Fleet's notice actions draw it: `primary` filled, `danger` a red wash, `quiet` (the default) text only.
 * It lives in tool rows' `<summary>`, so a click must not toggle the row or follow a link.
 */
const props = defineProps<{ node: ModControlNode }>();

const context = inject(MOD_TREE);
const tone = computed(() => {
  const value = str(props.node.props.tone);
  return value === "primary" || value === "danger" ? value : "quiet";
});
const icon = computed(() => modIcon(props.node.props.icon));
const disabled = computed(() => flag(props.node.props.disabled));

function press(): void {
  const handle = props.node.handles.onPress;
  if (!disabled.value && handle) context?.send({ handle, kind: "press" });
}
</script>

<template>
  <button
    type="button"
    class="mod-button"
    :class="`mod-button--${tone}`"
    :disabled="disabled"
    @click.stop.prevent="press"
  >
    <component
      :is="icon"
      v-if="icon"
      aria-hidden="true"
    />
    <span class="mod-button__label">{{ str(node.props.label) }}</span>
  </button>
</template>

<style scoped>
.mod-button {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  min-width: 0;
  max-width: 100%;
  height: 26px;
  padding: 0 10px;
  border: 0;
  border-radius: calc(var(--radius-btn) - 1px);
  font: inherit;
  font-size: 12px;
  font-weight: 500;
  white-space: nowrap;
  cursor: pointer;
  transition: background var(--transition), color var(--transition);
}

.mod-button__label {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mod-button svg {
  flex: none;
  width: 12px;
  height: 12px;
}

.mod-button--primary {
  background: var(--accent);
  color: var(--primary-foreground);
}

.mod-button--primary:hover:not(:disabled) {
  background: color-mix(in srgb, var(--accent) 88%, #000);
}

.mod-button--danger {
  background: color-mix(in srgb, var(--error) 16%, transparent);
  color: var(--error);
}

.mod-button--danger:hover:not(:disabled) {
  background: color-mix(in srgb, var(--error) 24%, transparent);
}

.mod-button--quiet {
  background: transparent;
  color: var(--muted);
}

.mod-button--quiet:hover:not(:disabled) {
  background: color-mix(in srgb, var(--text) 6%, transparent);
  color: var(--text);
}

.mod-button:disabled {
  opacity: 0.5;
  cursor: default;
}

.mod-button:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}
</style>
