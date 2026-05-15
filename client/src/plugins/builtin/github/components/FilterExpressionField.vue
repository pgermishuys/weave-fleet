<script setup lang="ts">
import { ref, computed } from "vue";
import { Search, X, Loader2 } from "lucide-vue-next";
import { parseFilterExpression, serializeFilterExpression } from "../lib/filter-expression";
import { DEFAULT_ISSUE_FILTER, type IssueFilterState } from "../composables/github-types";

const props = defineProps<{
  filter: IssueFilterState;
  isSearching?: boolean;
}>();

const emit = defineEmits<{
  change: [filter: IssueFilterState];
}>();

const serialized = computed(() => serializeFilterExpression(props.filter));
const editingValue = ref<string | null>(null);
const inputRef = ref<HTMLInputElement | null>(null);

const localValue = computed(() => editingValue.value ?? serialized.value);
const hasFilters = computed(() => serialized.value.length > 0);

function commit() {
  const parsed = parseFilterExpression(editingValue.value ?? serialized.value);
  editingValue.value = null;
  emit("change", parsed);
}

function handleFocus() {
  if (editingValue.value === null) {
    editingValue.value = serialized.value;
  }
}

function handleInput(e: Event) {
  editingValue.value = (e.target as HTMLInputElement).value;
}

function handleKeydown(e: KeyboardEvent) {
  if (e.key === "Enter") {
    e.preventDefault();
    commit();
    inputRef.value?.blur();
  } else if (e.key === "Escape") {
    e.preventDefault();
    editingValue.value = null;
    inputRef.value?.blur();
  }
}

function handleClear() {
  emit("change", { ...DEFAULT_ISSUE_FILTER });
  editingValue.value = null;
}
</script>

<template>
  <div class="expression-field">
    <span class="expression-icon">
      <Loader2 v-if="isSearching" :size="13" class="animate-spin" />
      <Search v-else :size="13" />
    </span>
    <input
      ref="inputRef"
      class="expression-input"
      :value="localValue"
      placeholder="Filter issues… e.g. is:open label:bug author:octocat"
      @input="handleInput"
      @focus="handleFocus"
      @blur="commit"
      @keydown="handleKeydown"
    />
    <button
      v-if="hasFilters"
      class="expression-clear"
      aria-label="Clear filters"
      @click="handleClear"
    >
      <X :size="11" />
    </button>
  </div>
</template>

<style scoped>
.expression-field {
  position: relative;
  flex: 1;
  display: flex;
  align-items: center;
}

.expression-icon {
  position: absolute;
  left: 8px;
  display: flex;
  align-items: center;
  color: var(--muted);
  pointer-events: none;
}

.expression-input {
  width: 100%;
  height: 28px;
  padding: 0 28px 0 28px;
  border: 1px solid var(--border);
  border-radius: 6px;
  background: var(--surface, var(--sidebar));
  color: var(--text);
  font-size: 11px;
  font-family: var(--font-mono, monospace);
  outline: none;
}

.expression-input:focus {
  border-color: var(--accent);
}

.expression-input::placeholder {
  color: var(--muted);
}

.expression-clear {
  position: absolute;
  right: 4px;
  display: flex;
  align-items: center;
  justify-content: center;
  width: 20px;
  height: 20px;
  border: none;
  background: transparent;
  color: var(--muted);
  cursor: pointer;
  border-radius: 4px;
}

.expression-clear:hover {
  color: var(--text);
  background: var(--sidebar-item-hover);
}
</style>
