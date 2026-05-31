<script setup lang="ts">
import { computed, ref, watch, nextTick } from "vue";
import { AlertCircle, Bot, FileText, Folder, LoaderCircle, Terminal } from "lucide-vue-next";
import type { AutocompleteItem } from "@/composables/use-autocomplete";

defineOptions({
  name: "AutocompletePopup",
});

interface AutocompletePopupProps {
  open: boolean;
  items: AutocompleteItem[];
  isLoading: boolean;
  selectedValue: string | null;
  error?: string;
  onSelect: (value: string) => void;
}

interface ItemGroup {
  key: AutocompleteItem["group"];
  label: string;
  items: AutocompleteItem[];
}

const props = defineProps<AutocompletePopupProps>();

const popupRef = ref<HTMLElement | null>(null);

watch(() => props.selectedValue, (val) => {
  if (!val || !popupRef.value) return;
  nextTick(() => {
    const escaped = typeof CSS !== "undefined" && CSS.escape ? CSS.escape(val) : val.replace(/([^\w-])/g, "\\$1");
    const el = popupRef.value?.querySelector(`[data-value="${escaped}"]`);
    el?.scrollIntoView({ block: "nearest" });
  });
});

const groupDefinitions: ReadonlyArray<{ key: AutocompleteItem["group"]; label: string }> = [
  { key: "command", label: "Commands" },
  { key: "agent", label: "Agents" },
  { key: "file", label: "Files" },
];

const groupedItems = computed<ItemGroup[]>(() => {
  return groupDefinitions
    .map((groupDefinition) => ({
      key: groupDefinition.key,
      label: groupDefinition.label,
      items: props.items.filter((item) => item.group === groupDefinition.key),
    }))
    .filter((group) => group.items.length > 0);
});

function isSelected(item: AutocompleteItem): boolean {
  return item.value === props.selectedValue;
}

function handleItemMouseDown(event: MouseEvent): void {
  event.preventDefault();
}

function handleSelect(value: string): void {
  props.onSelect(value);
}
</script>

<template>
  <div
    v-if="open"
    ref="popupRef"
    class="autocomplete-popup"
    role="listbox"
    aria-label="Autocomplete suggestions"
  >
    <div
      v-if="isLoading"
      class="autocomplete-popup__state"
    >
      <LoaderCircle
        class="autocomplete-popup__spinner"
        aria-hidden="true"
      />
      <span>Loading suggestions…</span>
    </div>

    <div
      v-else-if="error"
      class="autocomplete-popup__state autocomplete-popup__state--error"
      role="alert"
    >
      <AlertCircle
        class="autocomplete-popup__state-icon"
        aria-hidden="true"
      />
      <span>{{ error }}</span>
    </div>

    <div
      v-else-if="groupedItems.length === 0"
      class="autocomplete-popup__state"
    >
      No results
    </div>

    <div v-else>
      <section
        v-for="group in groupedItems"
        :key="group.key"
        class="autocomplete-popup__group"
      >
        <div class="autocomplete-popup__group-label">
          {{ group.label }}
        </div>

        <button
          v-for="item in group.items"
          :key="item.id"
          :data-value="item.value"
          type="button"
          class="autocomplete-popup__item"
          :class="{ 'autocomplete-popup__item--selected': isSelected(item) }"
          :aria-selected="isSelected(item)"
          @mousedown="handleItemMouseDown"
          @click="handleSelect(item.value)"
        >
          <span class="autocomplete-popup__icon-wrap">
            <Terminal
              v-if="item.group === 'command'"
              class="autocomplete-popup__icon"
              aria-hidden="true"
            />

            <span
              v-else-if="item.group === 'agent'"
              class="autocomplete-popup__agent-icon-wrap"
            >
              <Bot
                class="autocomplete-popup__icon"
                aria-hidden="true"
              />
              <span
                v-if="item.meta"
                class="autocomplete-popup__agent-dot"
                :style="{ backgroundColor: item.meta }"
                aria-hidden="true"
              />
            </span>

            <Folder
              v-else-if="item.meta === 'dir'"
              class="autocomplete-popup__icon"
              aria-hidden="true"
            />

            <FileText
              v-else
              class="autocomplete-popup__icon"
              aria-hidden="true"
            />
          </span>

          <span class="autocomplete-popup__content">
            <span class="autocomplete-popup__label">{{ item.label }}</span>
            <span
              v-if="item.description"
              class="autocomplete-popup__description"
            >{{ item.description }}</span>
          </span>
        </button>
      </section>
    </div>
  </div>
</template>

<style scoped>
.autocomplete-popup {
  position: absolute;
  right: 0;
  bottom: calc(100% + 8px);
  left: 0;
  max-height: 300px;
  overflow-y: auto;
  overscroll-behavior: contain;
  scrollbar-gutter: stable;
  border: 1px solid var(--border);
  border-radius: 12px;
  background: var(--card-bg);
  box-shadow: 0 10px 30px rgba(0, 0, 0, 0.3);
  z-index: 250;
}

.autocomplete-popup__group + .autocomplete-popup__group {
  border-top: 1px solid var(--border);
}

.autocomplete-popup__group-label {
  position: sticky;
  top: 0;
  padding: 8px 12px 6px;
  background: var(--card-bg);
  color: var(--muted);
  font-size: 10px;
  font-weight: 600;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}

.autocomplete-popup__item {
  display: flex;
  width: 100%;
  align-items: flex-start;
  gap: 10px;
  padding: 9px 12px;
  border: 0;
  background: transparent;
  color: inherit;
  cursor: pointer;
  text-align: left;
}

.autocomplete-popup__item:hover,
.autocomplete-popup__item--selected {
  background: color-mix(in srgb, var(--accent) 14%, transparent);
}

.autocomplete-popup__item:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.autocomplete-popup__icon-wrap,
.autocomplete-popup__agent-icon-wrap {
  position: relative;
  display: inline-flex;
  flex-shrink: 0;
  align-items: center;
  justify-content: center;
  width: 18px;
  height: 18px;
  margin-top: 1px;
}

.autocomplete-popup__icon {
  width: 16px;
  height: 16px;
  color: var(--muted);
}

.autocomplete-popup__agent-dot {
  position: absolute;
  right: -1px;
  bottom: -1px;
  width: 7px;
  height: 7px;
  border: 1px solid var(--card-bg);
  border-radius: 999px;
}

.autocomplete-popup__content {
  display: flex;
  min-width: 0;
  flex: 1;
  flex-direction: column;
  gap: 2px;
}

.autocomplete-popup__label {
  color: var(--text);
  font-size: 11px;
  font-weight: 600;
  line-height: 1.4;
  word-break: break-word;
}

.autocomplete-popup__description {
  color: var(--muted);
  font-size: 10px;
  line-height: 1.4;
  word-break: break-word;
}

.autocomplete-popup__state {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  padding: 14px 16px;
  color: var(--muted);
  font-size: 11px;
  text-align: center;
}

.autocomplete-popup__state--error {
  color: var(--error);
}

.autocomplete-popup__state-icon,
.autocomplete-popup__spinner {
  width: 16px;
  height: 16px;
  flex-shrink: 0;
}

.autocomplete-popup__spinner {
  animation: autocomplete-popup-spin 1s linear infinite;
}

@keyframes autocomplete-popup-spin {
  from {
    transform: rotate(0deg);
  }

  to {
    transform: rotate(360deg);
  }
}
</style>
