<script setup lang="ts">
import { computed } from "vue";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
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
}

interface Emits {
  "update:from": [value: string];
  "update:to": [value: string];
  "update:projectId": [value: string];
  reset: [];
}

const ALL_PROJECTS_VALUE = "__all_projects__";

const props = defineProps<Props>();
const emit = defineEmits<Emits>();

const integerFormatter = new Intl.NumberFormat("en-US");

const selectedProject = computed(() => {
  return props.projects.find((project) => project.id === props.projectId) ?? null;
});

const selectedProjectValue = computed({
  get: () => props.projectId || ALL_PROJECTS_VALUE,
  set: (value: string) => {
    emit("update:projectId", value === ALL_PROJECTS_VALUE ? "" : value);
  },
});

const projectHint = computed(() => {
  if (!selectedProject.value) {
    return "Leave blank to include every project.";
  }

  return `${integerFormatter.format(selectedProject.value.tokens)} tokens in ${formatAnalyticsCost(selectedProject.value.cost)} spend`;
});

function formatAnalyticsCost(cost: number): string {
  if (cost === 0) {
    return "$0.00";
  }

  if (cost < 0.01) {
    return `$${cost.toFixed(3)}`;
  }

  return `$${cost.toLocaleString("en-US", {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  })}`;
}

function handleFromUpdate(value: string | number): void {
  emit("update:from", String(value));
}

function handleToUpdate(value: string | number): void {
  emit("update:to", String(value));
}
</script>

<template>
  <div
    class="analytics-filters"
    role="group"
    aria-label="Analytics filters"
  >
    <label
      class="analytics-filter"
      for="analytics-filter-from"
    >
      <span class="analytics-filter__label">From</span>
      <Input
        id="analytics-filter-from"
        class="w-full"
        type="date"
        :model-value="from"
        @update:model-value="handleFromUpdate"
      />
    </label>

    <label
      class="analytics-filter"
      for="analytics-filter-to"
    >
      <span class="analytics-filter__label">To</span>
      <Input
        id="analytics-filter-to"
        class="w-full"
        type="date"
        :model-value="to"
        @update:model-value="handleToUpdate"
      />
    </label>

    <div class="analytics-filter analytics-filter--wide">
      <label
        class="analytics-filter__label"
        for="analytics-filter-project"
      >
        Project
      </label>
      <Select v-model="selectedProjectValue">
        <SelectTrigger
          id="analytics-filter-project"
          class="w-full bg-[var(--surface-2,rgba(255,255,255,0.03))]"
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
      <span class="analytics-filter__hint">{{ projectHint }}</span>
    </div>

    <div class="analytics-filters__actions">
      <Button
        type="button"
        variant="outline"
        class="w-full"
        @click="emit('reset')"
      >
        Reset filters
      </Button>
    </div>
  </div>
</template>

<style scoped>
.analytics-filters {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 12px;
  min-width: min(100%, 720px);
}

.analytics-filter {
  display: flex;
  min-width: 0;
  flex-direction: column;
  gap: 6px;
}

.analytics-filter--wide {
  min-width: 0;
}

.analytics-filter__label {
  color: var(--muted);
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.08em;
  text-transform: uppercase;
}

.analytics-filter__hint {
  color: var(--muted);
  font-size: 12px;
  line-height: 1.4;
}

.analytics-filters__actions {
  display: flex;
  align-items: flex-end;
}

@media (max-width: 1100px) {
  .analytics-filters {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

@media (max-width: 720px) {
  .analytics-filters {
    grid-template-columns: 1fr;
  }
}
</style>
