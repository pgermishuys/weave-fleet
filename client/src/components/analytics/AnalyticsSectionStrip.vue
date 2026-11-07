<script setup lang="ts">
import { shallowRef } from "vue";
import { SlidersHorizontal } from "lucide-vue-next";
import AnalyticsFilters from "@/components/analytics/AnalyticsFilters.vue";
import MachineSwitch from "@/components/layout/MachineSwitch.vue";
import { useAnalyticsMachine } from "@/composables/use-analytics-machine";
import { useAnalyticsMenu } from "@/composables/use-analytics-menu";

// The analytics-menu on a phone, where the context panel is a drawer: the sections as chips, the filters behind one.
const menu = useAnalyticsMenu();
const machine = useAnalyticsMachine();
const filtersOpen = shallowRef(false);
</script>

<template>
  <div class="analytics-strip">
    <MachineSwitch
      v-if="machine.enabled.value"
      :choice="machine"
      label="Analytics"
      testid="analytics-strip"
    />
    <nav
      class="analytics-strip__chips"
      aria-label="Analytics sections"
    >
      <button
        v-for="item in menu.items.value"
        :key="item.id"
        type="button"
        class="analytics-strip__chip"
        :class="{ 'analytics-strip__chip--active': menu.active.value === item.id }"
        :aria-current="menu.active.value === item.id ? 'page' : undefined"
        @click="menu.select(item.id)"
      >
        {{ item.label }}
      </button>
      <button
        type="button"
        class="analytics-strip__chip"
        :class="{ 'analytics-strip__chip--active': filtersOpen || !menu.isDefault.value }"
        :aria-expanded="filtersOpen"
        @click="filtersOpen = !filtersOpen"
      >
        <SlidersHorizontal
          :size="13"
          aria-hidden="true"
        />
        Filters
      </button>
    </nav>

    <AnalyticsFilters
      v-if="filtersOpen"
      :from="menu.filters.value.from"
      :to="menu.filters.value.to"
      :project-id="menu.filters.value.projectId"
      :projects="menu.projects.value"
      :is-default="menu.isDefault.value"
      @update:from="menu.setFrom"
      @update:to="menu.setTo"
      @update:project-id="menu.setProjectId"
      @reset="menu.resetFilters"
    />
  </div>
</template>

<style scoped>
.analytics-strip {
  flex-direction: column;
  gap: 10px;
}

.analytics-strip__chips {
  display: flex;
  gap: 6px;
  overflow-x: auto;
  padding-bottom: 2px;
}

.analytics-strip__chip {
  display: inline-flex;
  flex-shrink: 0;
  align-items: center;
  gap: 6px;
  height: 32px;
  padding: 0 12px;
  border: 1px solid var(--border);
  border-radius: 999px;
  background: var(--card-bg);
  color: var(--muted);
  font-size: 12.5px;
  font-weight: 500;
}

.analytics-strip__chip--active {
  border-color: color-mix(in srgb, var(--accent) 40%, transparent);
  color: var(--text);
}
</style>
