<script setup lang="ts">
import { computed, inject, useId } from "vue";
import { MOD_TREE } from "@/components/mods/mod-context";
import { str, type ModControlNode } from "@/components/mods/mod-style";

/** A drop-down, styled as Fleet's settings selects. */
const props = defineProps<{ node: ModControlNode }>();

const context = inject(MOD_TREE);
const id = useId();
const options = computed(() => {
  const list = props.node.props.options;
  return Array.isArray(list)
    ? list.flatMap((o) => (o && typeof o === "object" && !Array.isArray(o) && typeof o.value === "string"
      ? [{ value: o.value, label: typeof o.label === "string" ? o.label : o.value }]
      : []))
    : [];
});

function change(event: Event): void {
  const handle = props.node.handles.onSelect;
  if (handle) context?.send({ handle, kind: "select", value: (event.target as HTMLSelectElement).value });
}
</script>

<template>
  <div class="mod-select">
    <label
      v-if="str(node.props.label)"
      class="mod-select__label"
      :for="id"
    >{{ str(node.props.label) }}</label>
    <select
      :id="id"
      class="mod-select__field"
      :value="str(node.props.value)"
      @change="change"
    >
      <option
        v-for="option in options"
        :key="option.value"
        :value="option.value"
        :selected="option.value === str(node.props.value)"
      >
        {{ option.label }}
      </option>
    </select>
  </div>
</template>

<style scoped>
.mod-select {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
}

.mod-select__label {
  color: var(--muted);
  font-size: 12px;
}

.mod-select__field {
  height: 30px;
  min-width: 0;
  max-width: 100%;
  padding: 0 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
  color: var(--text);
  font: inherit;
  font-size: 12.5px;
  cursor: pointer;
}

.mod-select__field:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}
</style>
