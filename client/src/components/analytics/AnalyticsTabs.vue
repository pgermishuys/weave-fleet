<script setup lang="ts">
export type AnalyticsTabId = "overview" | "projects" | "sessions" | "models";

interface AnalyticsTabOption {
  id: AnalyticsTabId;
  label: string;
}

interface Props {
  activeTab: AnalyticsTabId;
}

interface Emits {
  select: [tabId: AnalyticsTabId];
}

const ANALYTICS_TABS: readonly AnalyticsTabOption[] = [
  { id: "overview", label: "Overview" },
  { id: "projects", label: "Projects" },
  { id: "sessions", label: "Sessions" },
  { id: "models", label: "Models" },
];

const props = defineProps<Props>();
const emit = defineEmits<Emits>();

function handleSelect(tabId: AnalyticsTabId): void {
  if (tabId === props.activeTab) {
    return;
  }

  emit("select", tabId);
}
</script>

<template>
  <nav
    class="analytics-tabs"
    aria-label="Analytics views"
  >
    <div
      class="analytics-tabs__list"
      role="tablist"
    >
      <button
        v-for="tab in ANALYTICS_TABS"
        :key="tab.id"
        type="button"
        class="analytics-tabs__trigger"
        :class="{ 'analytics-tabs__trigger--active': activeTab === tab.id }"
        role="tab"
        :aria-selected="activeTab === tab.id"
        :tabindex="activeTab === tab.id ? 0 : -1"
        @click="handleSelect(tab.id)"
      >
        {{ tab.label }}
      </button>
    </div>
  </nav>
</template>

<style scoped>
/* A segmented control: 32px, like the filters beside it. */
.analytics-tabs {
  display: flex;
  max-width: 100%;
}

.analytics-tabs__list {
  display: inline-flex;
  gap: 2px;
  height: 32px;
  padding: 2px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
}

.analytics-tabs__trigger {
  min-width: 0;
  padding: 0 12px;
  border: 0;
  border-radius: calc(var(--radius-btn) - 2px);
  background: transparent;
  color: var(--muted);
  font-size: 12.5px;
  font-weight: 500;
  cursor: pointer;
  transition: background-color var(--transition), color var(--transition);
}

.analytics-tabs__trigger:hover {
  color: var(--text);
}

.analytics-tabs__trigger:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.analytics-tabs__trigger--active {
  background: color-mix(in srgb, var(--text) 9%, transparent);
  color: var(--text);
}

@media (prefers-reduced-motion: reduce) {
  .analytics-tabs__trigger {
    transition: none;
  }
}
</style>
