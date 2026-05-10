<script setup lang="ts">
import { watch } from "vue";
import { ArrowUpDown, Check, Group, Search } from "lucide-vue-next";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";

export type GroupBy = "directory" | "session-status" | "connection-status" | "source" | "none";
export type SortBy = "recent" | "name" | "status";

interface Props {
  groupBy: GroupBy;
  sortBy: SortBy;
  search: string;
}

interface Emits {
  "update:groupBy": [value: GroupBy];
  "update:sortBy": [value: SortBy];
  "update:search": [value: string];
}

const props = defineProps<Props>();
const emit = defineEmits<Emits>();

const PREFS_KEY = "weave:fleet:prefs";

interface FleetPrefs {
  groupBy: GroupBy;
  sortBy: SortBy;
}

const GROUP_BY_LABELS: Record<GroupBy, string> = {
  directory: "Directory",
  "session-status": "Session Status",
  "connection-status": "Connection Status",
  source: "Source",
  none: "None",
};

const SORT_BY_LABELS: Record<SortBy, string> = {
  recent: "Recent",
  name: "Name",
  status: "Status",
};

const GROUP_BY_KEYS = Object.keys(GROUP_BY_LABELS) as GroupBy[];
const SORT_BY_KEYS = Object.keys(SORT_BY_LABELS) as SortBy[];

function persistPrefs(): void {
  try {
    const prefs: FleetPrefs = { groupBy: props.groupBy, sortBy: props.sortBy };
    window.localStorage.setItem(PREFS_KEY, JSON.stringify(prefs));
  } catch {
    // localStorage unavailable
  }
}

watch(() => [props.groupBy, props.sortBy] as const, persistPrefs);
</script>

<template>
  <div class="toolbar">
    <div class="search-wrapper">
      <Search
        :size="13"
        class="search-icon"
        aria-hidden="true"
      />
      <input
        :value="search"
        type="search"
        placeholder="Search sessions…"
        class="search-input"
        aria-label="Search sessions"
        @input="emit('update:search', ($event.target as HTMLInputElement).value)"
      >
    </div>

    <div class="toolbar-dropdowns">
      <DropdownMenu>
        <DropdownMenuTrigger as-child>
          <button
            type="button"
            class="toolbar-btn"
            :aria-label="`Group by: ${GROUP_BY_LABELS[groupBy]}`"
          >
            <Group
              :size="12"
              aria-hidden="true"
            />
            <span>Group: {{ GROUP_BY_LABELS[groupBy] }}</span>
          </button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="start">
          <DropdownMenuItem
            v-for="key in GROUP_BY_KEYS"
            :key="key"
            class="dropdown-item"
            @click="emit('update:groupBy', key)"
          >
            <Check
              v-if="groupBy === key"
              :size="12"
              aria-hidden="true"
            />
            <span
              v-else
              class="check-placeholder"
              aria-hidden="true"
            />
            {{ GROUP_BY_LABELS[key] }}
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>

      <DropdownMenu>
        <DropdownMenuTrigger as-child>
          <button
            type="button"
            class="toolbar-btn"
            :aria-label="`Sort by: ${SORT_BY_LABELS[sortBy]}`"
          >
            <ArrowUpDown
              :size="12"
              aria-hidden="true"
            />
            <span>Sort: {{ SORT_BY_LABELS[sortBy] }}</span>
          </button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="start">
          <DropdownMenuItem
            v-for="key in SORT_BY_KEYS"
            :key="key"
            class="dropdown-item"
            @click="emit('update:sortBy', key)"
          >
            <Check
              v-if="sortBy === key"
              :size="12"
              aria-hidden="true"
            />
            <span
              v-else
              class="check-placeholder"
              aria-hidden="true"
            />
            {{ SORT_BY_LABELS[key] }}
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>
    </div>
  </div>
</template>

<style scoped>
.toolbar {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 8px 8px 0;
}

.search-wrapper {
  position: relative;
}

.search-icon {
  position: absolute;
  left: 8px;
  top: 50%;
  transform: translateY(-50%);
  color: var(--muted);
  pointer-events: none;
}

.search-input {
  width: 100%;
  height: 28px;
  padding: 0 8px 0 28px;
  background: rgba(255, 255, 255, 0.04);
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  color: var(--text);
  font-size: 11px;
  outline: none;
  box-sizing: border-box;
}

.search-input::placeholder {
  color: var(--muted);
}

.search-input:focus {
  border-color: var(--accent);
}

.toolbar-dropdowns {
  display: flex;
  gap: 4px;
}

.toolbar-btn {
  display: flex;
  align-items: center;
  gap: 4px;
  height: 24px;
  padding: 0 8px;
  background: rgba(255, 255, 255, 0.04);
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  color: var(--muted);
  font-size: 10px;
  cursor: pointer;
  white-space: nowrap;
  transition: color 0.15s ease, border-color 0.15s ease;
}

.toolbar-btn:hover {
  color: var(--text);
  border-color: color-mix(in srgb, var(--accent) 60%, transparent);
}

.dropdown-item {
  font-size: 11px;
  gap: 6px;
}

.check-placeholder {
  display: inline-block;
  width: 12px;
  height: 12px;
  flex-shrink: 0;
}
</style>
