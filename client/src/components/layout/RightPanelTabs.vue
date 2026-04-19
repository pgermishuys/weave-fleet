<script setup lang="ts">
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
}>();

function handleSelect(tabId: string): void {
  if (tabId === props.activeTab) {
    return;
  }

  emit("select", tabId);
}
</script>

<template>
  <div class="right-tabs" role="tablist" aria-label="Right panel tabs">
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
  </div>
</template>

<style scoped>
.right-tabs {
  display: flex;
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
</style>
