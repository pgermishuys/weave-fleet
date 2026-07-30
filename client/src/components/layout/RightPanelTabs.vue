<script setup lang="ts">
import { PanelRightClose } from "lucide-vue-next";
import { Button } from "@/components/ui/button";

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

    <Button
      variant="toolbar-icon"
      size="toolbar"
      class="right-tabs__collapse"
      aria-label="Collapse right panel"
      @click="handleCollapse"
    >
      <PanelRightClose
        :size="14"
        aria-hidden="true"
      />
    </Button>
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
  padding: 8px 0;
  font-size: 10px;
  font-weight: 500;
  color: var(--muted);
  cursor: pointer;
  border: 0;
  border-bottom: 2px solid transparent;
  background: transparent;
  transition: color var(--transition), border-color var(--transition);
}

.right-tab:hover {
  color: var(--text);
}

.right-tab.active {
  color: var(--text);
  border-bottom-color: var(--accent);
}

.right-tabs__collapse {
  width: 34px;
  border-left: 1px solid var(--border);
}
</style>
