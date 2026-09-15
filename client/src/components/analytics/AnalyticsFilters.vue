<script setup lang="ts">
import { computed } from "vue";
import { RotateCcw } from "lucide-vue-next";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import type { AnalyticsProjectOption } from "@/composables/use-analytics-filters";

interface Props {
  from: string;
  to: string;
  projectId: string;
  projects: readonly AnalyticsProjectOption[];
  /** Nothing to reset: the filters are the defaults. */
  isDefault?: boolean;
}

interface Emits {
  "update:from": [value: string];
  "update:to": [value: string];
  "update:projectId": [value: string];
  reset: [];
}

const ALL_PROJECTS_VALUE = "__all_projects__";

const props = withDefaults(defineProps<Props>(), { isDefault: false });
const emit = defineEmits<Emits>();

const selectedProjectValue = computed({
  get: () => props.projectId || ALL_PROJECTS_VALUE,
  set: (value: string) => {
    emit("update:projectId", value === ALL_PROJECTS_VALUE ? "" : value);
  },
});

function onDate(event: Event, which: "update:from" | "update:to"): void {
  const value = (event.target as HTMLInputElement).value;
  if (which === "update:from") emit("update:from", value);
  else emit("update:to", value);
}
</script>

<template>
  <div
    class="analytics-filters"
    role="group"
    aria-label="Analytics filters"
  >
    <div class="analytics-filters__range">
      <label
        class="sr-only"
        for="analytics-filter-from"
      >From</label>
      <input
        id="analytics-filter-from"
        class="analytics-filters__date"
        type="date"
        :value="from"
        :max="to || undefined"
        @change="onDate($event, 'update:from')"
      >
      <span
        class="analytics-filters__dash"
        aria-hidden="true"
      >–</span>
      <label
        class="sr-only"
        for="analytics-filter-to"
      >To</label>
      <input
        id="analytics-filter-to"
        class="analytics-filters__date"
        type="date"
        :value="to"
        :min="from || undefined"
        @change="onDate($event, 'update:to')"
      >
    </div>

    <Select v-model="selectedProjectValue">
      <SelectTrigger
        id="analytics-filter-project"
        class="analytics-filters__project"
        aria-label="Project"
      >
        <SelectValue placeholder="All projects" />
      </SelectTrigger>

      <SelectContent>
        <SelectItem :value="ALL_PROJECTS_VALUE">
          All projects
        </SelectItem>
        <SelectItem
          v-for="project in projects"
          :key="project.id"
          :value="project.id"
        >
          {{ project.name }}
        </SelectItem>
      </SelectContent>
    </Select>

    <button
      type="button"
      class="analytics-filters__reset"
      :disabled="props.isDefault"
      title="Back to the last 30 days, every project"
      @click="emit('reset')"
    >
      <RotateCcw
        :size="13"
        aria-hidden="true"
      />
      Reset
    </button>
  </div>
</template>

<style scoped>
/* One row of 32px controls, the same height as the view switcher beside it. */
.analytics-filters {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
}

.analytics-filters__range {
  display: inline-flex;
  align-items: center;
  height: 32px;
  padding: 0 4px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
}

.analytics-filters__date {
  height: 30px;
  padding: 0 6px;
  border: 0;
  background: transparent;
  color: var(--text);
  font: inherit;
  font-size: 12.5px;
  font-variant-numeric: tabular-nums;
}

.analytics-filters__date::-webkit-calendar-picker-indicator {
  opacity: 0.55;
  cursor: pointer;
}

.analytics-filters__dash {
  color: var(--muted);
  font-size: 12px;
}

.analytics-filters :deep(.analytics-filters__project) {
  height: 32px;
  min-width: 160px;
  max-width: 220px;
  padding: 0 10px;
  border-color: var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
  font-size: 12.5px;
}

.analytics-filters__reset {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 32px;
  padding: 0 10px;
  border: 0;
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--muted);
  font-size: 12.5px;
  cursor: pointer;
  transition: background-color var(--transition), color var(--transition);
}

.analytics-filters__reset:hover:not(:disabled) {
  background: color-mix(in srgb, var(--text) 6%, transparent);
  color: var(--text);
}

.analytics-filters__reset:disabled {
  opacity: 0.45;
  cursor: default;
}

@media (prefers-reduced-motion: reduce) {
  .analytics-filters__reset {
    transition: none;
  }
}
</style>
