<script setup lang="ts">
import { computed, nextTick, ref, shallowRef, useTemplateRef, watch } from "vue";
import { onClickOutside } from "@vueuse/core";
import { ChevronDown } from "lucide-vue-next";

interface SelectorItem {
  id: string;
  label: string;
  description?: string;
  meta?: string;
}

const props = withDefaults(
  defineProps<{
    items: readonly SelectorItem[];
    label: string;
    placeholder?: string;
  }>(),
  {
    placeholder: "Select",
  },
);

const selectedId = defineModel<string>({ required: true });

const isOpen = shallowRef(false);
const rootRef = useTemplateRef<HTMLElement>("root");
const filterInputRef = useTemplateRef<HTMLInputElement>("filterInput");
const filterText = ref("");

onClickOutside(rootRef, () => {
  closeDropdown();
});

const selectedItem = computed(() => {
  return props.items.find((item) => item.id === selectedId.value) ?? null;
});

const normalizedLabel = computed(() => props.label.toLowerCase().replace(/ selector$/, ""));
const filterPlaceholder = computed(() => `Filter ${normalizedLabel.value}...`);
const filterAriaLabel = computed(() => `Filter ${props.label.toLowerCase()}`);

const filteredItems = computed(() => {
  const query = filterText.value.trim().toLowerCase();
  if (!query) {
    return props.items;
  }

  return props.items.filter((item) => {
    if (!item.id) {
      return true;
    }

    const searchValue = [item.label, item.description, item.meta, item.id]
      .filter((value): value is string => Boolean(value))
      .join(" ")
      .toLowerCase();

    return searchValue.includes(query);
  });
});

watch(isOpen, async (open) => {
  if (!open) {
    filterText.value = "";
    return;
  }

  await nextTick();
  filterInputRef.value?.focus();
});

function toggleOpen(): void {
  isOpen.value = !isOpen.value;
}

function closeDropdown(): void {
  isOpen.value = false;
  filterText.value = "";
}

function selectItem(itemId: string): void {
  selectedId.value = itemId;
  closeDropdown();
}
</script>

<template>
  <div
    ref="root"
    class="selector-dropdown-root"
    @keydown.escape.prevent="closeDropdown"
  >
    <button
      type="button"
      class="selector-btn"
      :aria-expanded="isOpen"
      aria-haspopup="listbox"
      :aria-label="label"
      @click="toggleOpen"
    >
      <span class="selector-btn__label">{{ selectedItem?.label ?? placeholder }}</span>
      <ChevronDown
        class="selector-btn__icon"
        :class="{ 'selector-btn__icon--open': isOpen }"
      />
    </button>

    <div
      v-if="isOpen"
      class="selector-dropdown"
      role="listbox"
      :aria-label="label"
    >
      <div class="selector-dropdown__filter-wrap">
        <input
          ref="filterInput"
          v-model="filterText"
          type="text"
          class="selector-dropdown__filter"
          :placeholder="filterPlaceholder"
          :aria-label="filterAriaLabel"
          @keydown.escape.prevent="closeDropdown"
        >
      </div>

      <button
        v-for="item in filteredItems"
        :key="item.id"
        type="button"
        class="selector-dropdown__item"
        :class="{ 'selector-dropdown__item--selected': item.id === selectedId }"
        :aria-selected="item.id === selectedId"
        @click="selectItem(item.id)"
      >
        <span class="selector-dropdown__copy">
          <span class="selector-dropdown__item-label">{{ item.label }}</span>
          <span
            v-if="item.description"
            class="selector-dropdown__item-description"
          >{{ item.description }}</span>
        </span>
        <span
          v-if="item.meta"
          class="selector-dropdown__item-meta"
        >{{ item.meta }}</span>
      </button>

      <div
        v-if="filteredItems.length === 0"
        class="selector-dropdown__empty"
      >
        No matching options
      </div>
    </div>
  </div>
</template>

<style scoped>
.selector-dropdown-root {
  position: relative;
}

.selector-btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 5px 12px;
  border: 1px solid var(--border);
  border-radius: 20px;
  background: transparent;
  color: var(--muted);
  cursor: pointer;
  font-size: 11px;
  line-height: 1.2;
}

.selector-btn:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.selector-btn__label {
  color: var(--text);
  white-space: nowrap;
}

.selector-btn__icon {
  width: 14px;
  height: 14px;
  transition: transform 0.2s ease;
}

.selector-btn__icon--open {
  transform: rotate(180deg);
}

.selector-dropdown {
  position: absolute;
  bottom: calc(100% + 6px);
  left: 0;
  min-width: 240px;
  max-height: min(320px, 60vh);
  overflow-y: auto;
  overscroll-behavior: contain;
  scrollbar-gutter: stable;
  padding: 4px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
  z-index: 200;
  box-shadow: 0 10px 30px rgba(0, 0, 0, 0.3);
}

.selector-dropdown__filter-wrap {
  position: sticky;
  top: 0;
  padding: 4px;
  margin: -4px -4px 4px;
  background: var(--card-bg);
  z-index: 1;
}

.selector-dropdown__filter {
  width: 100%;
  padding: 8px 10px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: rgba(255, 255, 255, 0.02);
  color: var(--text);
  font-size: 11px;
}

.selector-dropdown__filter::placeholder {
  color: var(--muted);
}

.selector-dropdown__filter:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -1px;
}

.selector-dropdown__item {
  width: 100%;
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  padding: 8px 10px;
  border: 0;
  border-radius: 8px;
  background: transparent;
  color: inherit;
  cursor: pointer;
  text-align: left;
}

.selector-dropdown__item:hover,
.selector-dropdown__item--selected {
  background: rgba(255, 255, 255, 0.05);
}

.selector-dropdown__item:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.selector-dropdown__copy {
  display: flex;
  min-width: 0;
  flex-direction: column;
  gap: 2px;
}

.selector-dropdown__item-label {
  color: var(--text);
  font-size: 11px;
  font-weight: 600;
}

.selector-dropdown__item-description {
  color: var(--muted);
  font-size: 10px;
  line-height: 1.4;
}

.selector-dropdown__item-meta {
  color: var(--muted);
  font-size: 10px;
  white-space: nowrap;
}

.selector-dropdown__empty {
  padding: 10px;
  color: var(--muted);
  font-size: 11px;
  text-align: center;
}
</style>
