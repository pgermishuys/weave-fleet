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
.analytics-tabs {
  display: flex;
  width: fit-content;
  max-width: 100%;
}

.analytics-tabs__list {
  display: inline-flex;
  width: 100%;
  max-width: 100%;
  gap: 4px;
  padding: 4px;
  border: 1px solid var(--border);
  border-radius: 16px;
  background: var(--card-bg);
  box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.03);
}

.analytics-tabs__trigger {
  flex: 1 1 0;
  min-width: 0;
  padding: 10px 16px;
  border: 0;
  border-radius: 12px;
  background: transparent;
  color: var(--muted);
  font-size: 12px;
  font-weight: 600;
  line-height: 1.2;
  cursor: pointer;
  transition:
    background-color 0.18s ease,
    color 0.18s ease,
    box-shadow 0.18s ease,
    transform 0.18s ease;
}

.analytics-tabs__trigger:hover {
  color: var(--text);
}

.analytics-tabs__trigger:focus-visible {
  outline: 2px solid rgba(99, 102, 241, 0.6);
  outline-offset: 2px;
}

.analytics-tabs__trigger--active {
  background: rgba(99, 102, 241, 0.18);
  color: var(--text);
  box-shadow: inset 0 0 0 1px rgba(99, 102, 241, 0.32);
}

@media (max-width: 720px) {
  .analytics-tabs {
    width: 100%;
  }

  .analytics-tabs__list {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}
</style>
