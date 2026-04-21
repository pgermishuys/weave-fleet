<script setup lang="ts">
import { PanelRightClose } from "lucide-vue-next";

interface RightPanelTabOption {
  id: string;
  label: string;
}

const props = defineProps<{
  tabs: readonly RightPanelTabOption[];
  activeTab: string;
}>();

const emit = defineEmits<{
  select: [tabId: string];
  collapse: [];
}>();

function handleSelect(tabId: string): void {
  if (tabId === props.activeTab) {
    return;
  }

  emit("select", tabId);
}

function handleCollapse(): void {
  emit("collapse");
}
</script>

<template>
  <div
    class="right-tabs"
    role="tablist"
    aria-label="Right panel tabs"
  >
    <button
      v-for="tab in tabs"
      :key="tab.id"
      type="button"
      class="right-tab"
      :class="{ active: activeTab === tab.id }"
      role="tab"
      :aria-selected="activeTab === tab.id"
      @click="handleSelect(tab.id)"
    >
      {{ tab.label }}
    </button>

    <button
      type="button"
      class="right-tabs__collapse"
      aria-label="Collapse right panel"
      @click="handleCollapse"
    >
      <PanelRightClose :size="14" aria-hidden="true" />
    </button>
  </div>
</template>

<style scoped>
.right-tabs {
  display: flex;
  align-items: stretch;
  border-bottom: 1px solid var(--border);
  flex-shrink: 0;
}

.right-tab {
  flex: 1;
  text-align: center;
  padding: 10px 0;
  font-size: 12px;
  font-weight: 500;
  color: var(--muted);
  cursor: pointer;
  border: 0;
  border-bottom: 2px solid transparent;
  background: transparent;
  transition: color 0.15s, border-color 0.15s;
}

.right-tab:hover {
  color: var(--text);
}

.right-tab.active {
  color: var(--text);
  border-bottom-color: var(--accent);
}

.right-tabs__collapse {
  width: 38px;
  border: 0;
  border-left: 1px solid var(--border);
  background: transparent;
  color: var(--muted);
  display: inline-flex;
  align-items: center;
  justify-content: center;
  cursor: pointer;
  transition: color 0.15s ease, background-color 0.15s ease;
}

.right-tabs__collapse:hover {
  color: var(--text);
  background: rgba(255, 255, 255, 0.04);
}

.right-tabs__collapse:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}
</style>
