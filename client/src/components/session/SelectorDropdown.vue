<script setup lang="ts">
import { computed, nextTick, ref, shallowRef, useId, useTemplateRef, watch } from "vue";
import { onClickOutside } from "@vueuse/core";
import { ChevronDown, Star } from "lucide-vue-next";

interface SelectorItem {
  id: string;
  label: string;
  description?: string;
  meta?: string;
  /** Defined: the row has a star to favourite it with. */
  starred?: boolean;
}

/** A provider heading's quota: a small bar, and the rows below dim (still pickable) when it's nearly used up. */
interface SectionUsage {
  percent: number;
  tone: "quiet" | "warn" | "used";
  dimmed: boolean;
  resetText?: string | null;
}

interface SelectorSection {
  title?: string;
  level?: "group" | "provider";
  usage?: SectionUsage | null;
  items: readonly SelectorItem[];
}

const props = withDefaults(
  defineProps<{
    items: readonly SelectorItem[];
    /** Shown with their headers while nothing is typed; typing falls back to the flat `items`. */
    sections?: readonly SelectorSection[];
    label: string;
    placeholder?: string;
    disabled?: boolean;
    testId?: string;
  }>(),
  {
    placeholder: "Select",
    disabled: false,
    testId: undefined,
    sections: undefined,
  },
);

const emit = defineEmits<{ (event: "toggle-star", id: string): void }>();

const selectedId = defineModel<string>({ required: true });

const isOpen = shallowRef(false);
const rootRef = useTemplateRef<HTMLElement>("root");
const filterInputRef = useTemplateRef<HTMLInputElement>("filterInput");
const filterText = ref("");
const activeIndex = shallowRef(0);
const listboxId = `selector-listbox-${useId()}`;
const optionId = (index: number) => `${listboxId}-option-${index}`;
const listRef = useTemplateRef<HTMLElement>("list");

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

/** What is drawn, in order: sections while nothing is typed, else one headerless run. Rows are numbered across sections. */
const view = computed(() => {
  const shown: readonly SelectorSection[] = !filterText.value.trim() && props.sections
    ? props.sections
    : [{ items: filteredItems.value }];
  let index = 0;
  return shown.map((section, sectionIndex) => ({
    title: section.title,
    level: section.level ?? "group",
    usage: section.usage ?? null,
    rows: section.items.map((item) => ({ item, key: `${sectionIndex}:${item.id}`, index: index++ })),
  }));
});
const rows = computed(() => view.value.flatMap((section) => section.rows));

// Typing puts Enter on the first match, not on "Default", which every filter keeps.
watch(filterText, (text) => {
  activeIndex.value = text.trim() ? Math.max(0, rows.value.findIndex((row) => row.item.id !== "")) : 0;
});

watch(activeIndex, async () => {
  await nextTick();
  // jsdom has no scrollIntoView.
  listRef.value?.querySelector<HTMLElement>("[data-active='true']")?.scrollIntoView?.({ block: "nearest" });
});

watch(isOpen, async (open) => {
  if (!open) {
    filterText.value = "";
    return;
  }

  activeIndex.value = Math.max(0, rows.value.findIndex((row) => row.item.id === selectedId.value));
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

function moveActive(step: 1 | -1): void {
  activeIndex.value = Math.min(Math.max(activeIndex.value + step, 0), rows.value.length - 1);
}

function pickActive(): void {
  const row = rows.value[activeIndex.value];
  if (row) selectItem(row.item.id);
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
      :disabled="disabled"
      :data-testid="testId"
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
      ref="list"
      class="selector-dropdown"
    >
      <div class="selector-dropdown__filter-wrap">
        <input
          ref="filterInput"
          v-model="filterText"
          type="text"
          class="selector-dropdown__filter"
          role="combobox"
          aria-expanded="true"
          aria-haspopup="listbox"
          aria-autocomplete="list"
          :aria-controls="listboxId"
          :aria-activedescendant="rows.length > 0 ? optionId(activeIndex) : undefined"
          :placeholder="filterPlaceholder"
          :aria-label="filterAriaLabel"
          @keydown.escape.prevent="closeDropdown"
          @keydown.down.prevent="moveActive(1)"
          @keydown.up.prevent="moveActive(-1)"
          @keydown.enter.prevent="pickActive"
        >
      </div>

      <!-- display: contents keeps the scrolling box as it was; the listbox holds only the options and their headings. -->
      <div
        :id="listboxId"
        class="selector-dropdown__listbox"
        role="listbox"
        :aria-label="label"
      >
      <template
        v-for="(section, sectionIndex) in view"
        :key="sectionIndex"
      >
        <div
          v-if="section.title"
          class="selector-dropdown__heading"
          role="presentation"
          :class="{ 'selector-dropdown__heading--provider': section.level === 'provider' }"
        >
          {{ section.title }}
          <span
            v-if="section.usage"
            class="selector-dropdown__usage"
            :class="`selector-dropdown__usage--${section.usage.tone}`"
            :title="`${section.usage.percent}% of the limit used`"
            data-testid="model-provider-usage"
          >
            <span
              v-if="section.usage.dimmed && section.usage.resetText"
              data-testid="model-provider-resets"
            >{{ section.usage.resetText }}</span>
            <span
              class="selector-dropdown__usage-bar"
              aria-hidden="true"
            ><i :style="{ width: `${section.usage.percent}%` }" /></span>
          </span>
        </div>
        <div
          v-for="{ item, key, index } in section.rows"
          :key="key"
          class="selector-dropdown__row"
          role="presentation"
          :class="{ 'selector-dropdown__row--active': index === activeIndex, 'selector-dropdown__row--dim': section.usage?.dimmed }"
          :data-active="index === activeIndex"
          @mousemove="activeIndex = index"
        >
          <button
            :id="optionId(index)"
            type="button"
            role="option"
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
          <button
            v-if="item.starred !== undefined"
            type="button"
            class="selector-dropdown__star"
            :class="{ 'selector-dropdown__star--on': item.starred }"
            :aria-label="item.starred ? 'Remove from favourites' : 'Add to favourites'"
            :aria-pressed="item.starred"
            @click.stop="emit('toggle-star', item.id)"
          >
            <Star aria-hidden="true" />
          </button>
        </div>
      </template>
      </div>

      <div
        v-if="rows.length === 0"
        class="selector-dropdown__empty"
      >
        No matching options
      </div>
    </div>
  </div>
</template>

<style scoped>
.selector-dropdown__usage {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  float: right;
  color: var(--muted);
  font-weight: 400;
  text-transform: none;
  letter-spacing: 0;
}

.selector-dropdown__usage--warn {
  color: var(--status-waiting);
}

.selector-dropdown__usage--used {
  color: var(--error);
}

.selector-dropdown__usage-bar {
  display: inline-block;
  overflow: hidden;
  width: 28px;
  height: 4px;
  border-radius: 999px;
  background: color-mix(in srgb, currentColor 22%, transparent);
}

.selector-dropdown__usage-bar i {
  display: block;
  height: 100%;
  border-radius: inherit;
  background: currentColor;
}

.selector-dropdown__row--dim {
  opacity: 0.55;
}

.selector-dropdown-root {
  position: relative;
  /* A long label ("Default (Claude Opus 4.7)") gives way in a tight toolbar instead of pushing it wider. */
  min-width: 0;
}

.selector-btn {
  display: inline-flex;
  max-width: 100%;
  align-items: center;
  gap: 5px;
  height: 28px;
  padding: 0 8px;
  border: 0;
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--muted);
  cursor: pointer;
  font-size: 12px;
  line-height: 1.2;
  transition: background var(--transition), color var(--transition);
}

.selector-btn:disabled {
  cursor: default;
  opacity: 0.6;
}

.selector-btn:hover:not(:disabled),
.selector-btn[aria-expanded="true"] {
  background: color-mix(in srgb, var(--text) 6%, transparent);
}

.selector-btn:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.selector-btn__label {
  min-width: 0;
  overflow: hidden;
  color: color-mix(in srgb, var(--text) 80%, transparent);
  text-overflow: ellipsis;
  white-space: nowrap;
}


.selector-btn__icon {
  flex-shrink: 0;
  width: 13px;
  height: 13px;
  transition: transform var(--transition);
}

.selector-btn__icon--open {
  transform: rotate(180deg);
}

.selector-dropdown__listbox {
  display: contents;
}

.selector-dropdown {
  position: absolute;
  bottom: calc(100% + 6px);
  left: 0;
  min-width: 280px;
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
  padding: 7px 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: color-mix(in srgb, var(--text) 3%, transparent);
  color: var(--text);
  font-size: 12px;
}

.selector-dropdown__filter::placeholder {
  color: var(--muted);
}

.selector-dropdown__filter:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -1px;
}

.selector-dropdown__heading {
  padding: 8px 10px 3px;
  color: var(--muted);
  font-size: 10px;
  font-weight: 600;
  letter-spacing: 0.06em;
  text-transform: uppercase;
}

.selector-dropdown__heading--provider {
  padding-left: 16px;
  font-size: 9.5px;
  font-weight: 500;
}

.selector-dropdown__row {
  display: flex;
  align-items: stretch;
  border-radius: calc(var(--radius-btn) - 2px);
}

.selector-dropdown__row--active {
  background: color-mix(in srgb, var(--text) 6%, transparent);
}

.selector-dropdown__star {
  display: flex;
  flex-shrink: 0;
  align-items: center;
  padding: 0 8px;
  border: 0;
  background: transparent;
  color: var(--muted);
  cursor: pointer;
  /* Only starred rows show it all the time. */
  opacity: 0;
}

.selector-dropdown__star svg {
  width: 14px;
  height: 14px;
}

.selector-dropdown__star--on,
.selector-dropdown__row--active .selector-dropdown__star,
.selector-dropdown__row:focus-within .selector-dropdown__star {
  opacity: 1;
}

.selector-dropdown__star--on {
  color: var(--idle);
}

.selector-dropdown__star--on svg {
  fill: currentColor;
}

.selector-dropdown__star:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.selector-dropdown__item {
  flex: 1;
  min-width: 0;
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  padding: 7px 10px;
  border: 0;
  border-radius: calc(var(--radius-btn) - 2px);
  background: transparent;
  color: inherit;
  cursor: pointer;
  text-align: left;
}

.selector-dropdown__item--selected {
  background: color-mix(in srgb, var(--text) 6%, transparent);
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
  font-size: 13px;
  font-weight: 500;
}

.selector-dropdown__item-description {
  color: var(--muted);
  font-size: 12px;
  line-height: 1.4;
}

.selector-dropdown__item-meta {
  color: var(--muted);
  font-size: 12px;
  white-space: nowrap;
}

.selector-dropdown__empty {
  padding: 10px;
  color: var(--muted);
  font-size: 12px;
  text-align: center;
}
</style>
