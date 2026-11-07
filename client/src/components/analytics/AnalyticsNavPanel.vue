<script setup lang="ts">
import { computed } from "vue";
import { Download } from "lucide-vue-next";
import AnalyticsFilters from "@/components/analytics/AnalyticsFilters.vue";
import MachineSwitch from "@/components/layout/MachineSwitch.vue";
import { useAnalyticsMachine } from "@/composables/use-analytics-machine";
import { useAnalyticsMenu } from "@/composables/use-analytics-menu";
import { ANALYTICS_GROUPS } from "@/lib/analytics-sections";

const menu = useAnalyticsMenu();
const machine = useAnalyticsMachine();

const groups = computed(() =>
  ANALYTICS_GROUPS.map((group) => ({
    ...group,
    items: menu.items.value.filter((item) => item.group === group.id),
  })),
);
</script>

<template>
  <section
    class="analytics-nav"
    aria-label="Analytics navigation"
  >
    <p class="analytics-nav__header">
      Analytics
    </p>
    <div
      v-if="machine.enabled.value"
      class="analytics-nav__machine"
    >
      <MachineSwitch
        :choice="machine"
        label="Analytics"
        testid="analytics"
      />
    </div>

    <nav
      class="analytics-nav__sections"
      aria-label="Analytics sections"
    >
      <div
        v-for="group in groups"
        :key="group.id"
        class="analytics-nav__group"
        role="group"
        :aria-label="group.label"
      >
        <p class="analytics-nav__group-label">
          {{ group.label }}
        </p>
        <button
          v-for="item in group.items"
          :key="item.id"
          type="button"
          class="analytics-nav__item"
          :class="{ 'analytics-nav__item--active': menu.active.value === item.id }"
          :aria-current="menu.active.value === item.id ? 'page' : undefined"
          :data-analytics-section="item.id"
          @click="menu.select(item.id)"
        >
          <span>{{ item.label }}</span>
          <span
            v-if="item.count !== null"
            class="analytics-nav__count"
          >{{ item.count }}</span>
        </button>
      </div>
    </nav>

    <div class="analytics-nav__filters">
      <p class="analytics-nav__group-label">
        Filters
      </p>
      <AnalyticsFilters
        stacked
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
      <a
        class="analytics-nav__export"
        :href="menu.exportHref.value"
        download="analytics.csv"
        @click="menu.exportCsv"
      >
        <Download
          :size="13"
          aria-hidden="true"
        />
        Export CSV
      </a>
    </div>
  </section>
</template>

<style scoped>
.analytics-nav__machine {
  padding: 0 12px 6px;
}

.analytics-nav {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
  overflow-y: auto;
  padding: 0 12px 12px;
}

.analytics-nav__header {
  margin: 0;
  padding: 18px 4px 10px;
  font-size: 0.71429rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--muted);
}

.analytics-nav__group {
  display: flex;
  flex-direction: column;
  gap: 4px;
  margin-bottom: 8px;
}

.analytics-nav__group-label {
  margin: 8px 0 2px;
  padding: 0 12px;
  font-size: 0.71429rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--muted);
  opacity: 0.8;
}

.analytics-nav__item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  width: 100%;
  min-height: 36px;
  padding: 0 12px;
  border: 1px solid transparent;
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--muted);
  font-size: 0.85714rem;
  font-weight: 500;
  text-align: left;
  transition: background-color var(--transition), border-color var(--transition), color var(--transition);
}

.analytics-nav__item:hover {
  background: var(--bg);
  color: var(--text);
}

.analytics-nav__item:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.analytics-nav__item--active {
  border-color: color-mix(in srgb, var(--accent) 40%, transparent);
  background: color-mix(in srgb, var(--panel-bg) 88%, var(--accent) 12%);
  color: var(--text);
}

.analytics-nav__count {
  font-size: 0.78571rem;
  font-variant-numeric: tabular-nums;
  color: var(--muted);
}

.analytics-nav__filters {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin-top: 4px;
  padding-top: 8px;
  border-top: 1px solid var(--border);
}

.analytics-nav__filters .analytics-nav__group-label {
  margin: 0;
  padding: 0 4px;
}

.analytics-nav__export {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 32px;
  padding: 0 10px;
  border-radius: var(--radius-btn);
  color: var(--muted);
  font-size: 12.5px;
  text-decoration: none;
}

.analytics-nav__export:hover {
  background: color-mix(in srgb, var(--text) 6%, transparent);
  color: var(--text);
}

.analytics-nav__export:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}
</style>
