<script setup lang="ts">
import { computed, ref, watch, nextTick } from "vue";
import { AlertCircle, Bot, ChevronRight, FileText, Folder, LoaderCircle, Terminal } from "lucide-vue-next";
import StatusGlyph from "@/components/sessions/StatusGlyph.vue";
import type { AutocompleteItem } from "@/composables/use-autocomplete";
import { useRelativeTime } from "@/composables/use-relative-time";
import { formatCompactAge } from "@/lib/session-row-status";

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
  /** Lists a folder's contents instead of referencing the folder. */
  onOpenFolder?: (value: string) => void;
  /** What's typed after the `@`, marked in session titles. */
  query?: string;
  /** What the agent gets for a session, shown under the list while a session is selected. */
  sessionNote?: string;
}

interface ItemGroup {
  key: AutocompleteItem["group"];
  label: string;
  /** Said on the right of the group's heading. */
  hint?: string;
  items: AutocompleteItem[];
}

/** Statuses that get a glyph on a session's row, as in the session list. */
const LIVE_STATUSES = new Set(["active", "waiting_input", "error"]);

const props = defineProps<AutocompletePopupProps>();

const popupRef = ref<HTMLElement | null>(null);

watch(() => props.selectedValue, (val) => {
  if (!val || !popupRef.value) return;
  nextTick(() => {
    const escaped = typeof CSS !== "undefined" && CSS.escape ? CSS.escape(val) : val.replace(/([^\w-])/g, "\\$1");
    const el = popupRef.value?.querySelector(`[data-value="${escaped}"]`);
    el?.scrollIntoView?.({ block: "nearest" });
  });
});

const groupDefinitions: ReadonlyArray<{ key: AutocompleteItem["group"]; label: string; hint?: string }> = [
  { key: "command", label: "Commands" },
  { key: "session", label: "Sessions", hint: "Tab to attach" },
  { key: "file", label: "Files & folders" },
  { key: "agent", label: "Agents" },
];

const groupedItems = computed<ItemGroup[]>(() => {
  return groupDefinitions
    .map((groupDefinition) => ({
      key: groupDefinition.key,
      label: groupDefinition.label,
      hint: groupDefinition.hint,
      items: props.items.filter((item) => item.group === groupDefinition.key),
    }))
    .filter((group) => group.items.length > 0);
});

const now = useRelativeTime();

/** A session title split around the first match of what's typed, so the match can be marked. */
function titleParts(title: string): { before: string; match: string; after: string } {
  const query = props.query?.trim() ?? "";
  const at = query ? title.toLowerCase().indexOf(query.toLowerCase()) : -1;
  return at < 0
    ? { before: title, match: "", after: "" }
    : { before: title.slice(0, at), match: title.slice(at, at + query.length), after: title.slice(at + query.length) };
}

const selectedItem = computed(() => props.items.find((item) => item.value === props.selectedValue) ?? null);

function isSelected(item: AutocompleteItem): boolean {
  return item.value === props.selectedValue;
}

function isFolder(item: AutocompleteItem): boolean {
  return item.group === "file" && item.meta === "dir";
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
    <!-- Results stay up while newer ones load; the states below only show when there's nothing to list. -->
    <div v-if="groupedItems.length > 0">
      <section
        v-for="group in groupedItems"
        :key="group.key"
        class="autocomplete-popup__group"
      >
        <div class="autocomplete-popup__group-label">
          <span>{{ group.label }}</span>
          <span
            v-if="group.hint"
            class="autocomplete-popup__group-hint"
          >{{ group.hint }}</span>
        </div>

        <div
          v-for="item in group.items"
          :key="item.id"
          class="autocomplete-popup__row"
          :class="{ 'autocomplete-popup__row--selected': isSelected(item) }"
        >
          <button
            :data-value="item.value"
            type="button"
            class="autocomplete-popup__item"
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

              <template v-else-if="item.session">
                <StatusGlyph
                  v-if="LIVE_STATUSES.has(item.session.status)"
                  :status="item.session.status"
                  :activity="item.session.activity"
                />
              </template>

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
                class="autocomplete-popup__icon autocomplete-popup__icon--folder"
                aria-hidden="true"
              />

              <FileText
                v-else
                class="autocomplete-popup__icon"
                aria-hidden="true"
              />
            </span>

            <span class="autocomplete-popup__content">
              <span
                v-if="item.session"
                class="autocomplete-popup__label"
                data-testid="autocomplete-session-title"
              >{{ titleParts(item.label).before }}<mark>{{ titleParts(item.label).match }}</mark>{{ titleParts(item.label).after }}</span>
              <span
                v-else
                class="autocomplete-popup__label"
              >{{ item.label }}</span>
              <span
                v-if="item.description"
                class="autocomplete-popup__description"
              >{{ item.description }}</span>
            </span>
            <span
              v-if="item.session"
              class="autocomplete-popup__age"
            >{{ formatCompactAge(item.session.updatedAt, now) }}</span>
          </button>

          <button
            v-if="isFolder(item) && onOpenFolder"
            type="button"
            class="autocomplete-popup__open"
            :aria-label="`Open ${item.label}`"
            title="Open folder (Tab)"
            @mousedown="handleItemMouseDown"
            @click="onOpenFolder(item.value)"
          >
            <ChevronRight
              class="autocomplete-popup__open-icon"
              aria-hidden="true"
            />
          </button>
        </div>
      </section>
    </div>

    <div
      v-else-if="isLoading"
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
      v-else
      class="autocomplete-popup__state"
    >
      No results
    </div>

    <div
      v-if="selectedItem?.group === 'session' && sessionNote"
      class="autocomplete-popup__hint autocomplete-popup__hint--note"
      data-testid="autocomplete-session-note"
    >
      {{ sessionNote }}
    </div>

    <div
      v-if="selectedItem?.group === 'file'"
      class="autocomplete-popup__hint"
    >
      <template v-if="isFolder(selectedItem)">
        <kbd>Tab</kbd> open folder · <kbd>Enter</kbd> reference it
      </template>
      <template v-else>
        <kbd>Enter</kbd> reference it
      </template>
    </div>
  </div>
</template>

<style scoped>
.autocomplete-popup {
  position: absolute;
  right: 0;
  bottom: calc(100% + 6px);
  left: 0;
  max-height: 340px;
  overflow-y: auto;
  overscroll-behavior: contain;
  padding: 6px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
  box-shadow: 0 16px 40px -12px rgba(0, 0, 0, 0.45);
  z-index: 250;
}

.autocomplete-popup__group + .autocomplete-popup__group {
  margin-top: 2px;
}

.autocomplete-popup__group-label {
  position: sticky;
  top: -6px;
  z-index: 1;
  display: flex;
  padding: 6px 8px 2px;
  background: var(--card-bg);
  color: var(--muted);
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}

.autocomplete-popup__group-hint {
  margin-left: auto;
  font-weight: 400;
  letter-spacing: 0;
  text-transform: none;
}

.autocomplete-popup__row {
  display: flex;
  align-items: stretch;
  border-radius: 7px;
}

.autocomplete-popup__row:hover,
.autocomplete-popup__row--selected {
  background: var(--accent-dim);
}

.autocomplete-popup__item {
  display: flex;
  min-width: 0;
  flex: 1;
  align-items: center;
  gap: 9px;
  padding: 6px 8px;
  border: 0;
  border-radius: 7px;
  background: transparent;
  color: inherit;
  cursor: pointer;
  text-align: left;
}

.autocomplete-popup__item:focus-visible,
.autocomplete-popup__open:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.autocomplete-popup__open {
  display: inline-grid;
  flex-shrink: 0;
  place-items: center;
  width: 32px;
  padding: 0;
  border: 0;
  border-radius: 7px;
  background: transparent;
  color: var(--muted);
  cursor: pointer;
}

.autocomplete-popup__open:hover {
  background: var(--accent-dim);
  color: var(--text);
}

.autocomplete-popup__open-icon {
  width: 14px;
  height: 14px;
}

.autocomplete-popup__hint {
  position: sticky;
  bottom: -6px;
  margin: 4px -6px -6px;
  padding: 7px 14px 8px;
  border-top: 1px solid var(--border);
  background: var(--card-bg);
  color: var(--muted);
  font-size: 12px;
}

.autocomplete-popup__hint kbd {
  padding: 0 4px;
  border: 1px solid var(--border);
  border-radius: 3px;
  font-family: inherit;
  font-size: 11px;
}

.autocomplete-popup__icon-wrap,
.autocomplete-popup__agent-icon-wrap {
  position: relative;
  display: inline-flex;
  flex-shrink: 0;
  align-items: center;
  justify-content: center;
  width: 16px;
  height: 16px;
}

.autocomplete-popup__icon {
  width: 14px;
  height: 14px;
  color: var(--muted);
}

.autocomplete-popup__icon--folder {
  color: var(--accent);
}

.autocomplete-popup__agent-dot {
  position: absolute;
  right: -1px;
  bottom: -1px;
  width: 7px;
  height: 7px;
  border: 1px solid var(--card-bg);
  border-radius: 50%;
}

/* One line: the name, then what it is in muted text, as the mockup's rows. */
.autocomplete-popup__content {
  display: flex;
  min-width: 0;
  flex: 1;
  align-items: baseline;
  gap: 9px;
}

.autocomplete-popup__label {
  flex-shrink: 1;
  min-width: 0;
  overflow: hidden;
  color: var(--text);
  font-size: 13.5px;
  line-height: 1.4;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.autocomplete-popup__label mark {
  background: none;
  color: var(--accent);
  font-weight: 600;
}

.autocomplete-popup__description {
  flex: 1 1 0;
  min-width: 0;
  overflow: hidden;
  color: var(--muted);
  font-size: 12px;
  line-height: 1.4;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.autocomplete-popup__age {
  flex-shrink: 0;
  color: var(--muted);
  font-size: 12px;
  font-variant-numeric: tabular-nums;
}

.autocomplete-popup__state {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  padding: 14px 16px;
  color: var(--muted);
  font-size: 12px;
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
