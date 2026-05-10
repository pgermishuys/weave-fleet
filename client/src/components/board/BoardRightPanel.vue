<script setup lang="ts">
import { shallowRef } from "vue";
import { storeToRefs } from "pinia";
import BoardActivityPanel from "@/components/board/BoardActivityPanel.vue";
import BoardSummaryPanel from "@/components/board/BoardSummaryPanel.vue";
import RightPanelTabs from "@/components/layout/RightPanelTabs.vue";
import { useSidebarStore } from "@/stores/sidebar";

const sidebarStore = useSidebarStore();
const { rightPanelCollapsed } = storeToRefs(sidebarStore);

const boardTabs = [
  {
    id: "summary",
    label: "Summary",
  },
  {
    id: "activity",
    label: "Activity",
  },
] as const;

type BoardTabId = (typeof boardTabs)[number]["id"];

const activeTabId = shallowRef<BoardTabId>("summary");

function handleTabSelect(tabId: string): void {
  if (tabId === "summary" || tabId === "activity") {
    activeTabId.value = tabId;
  }
}

function handleCollapse(): void {
  sidebarStore.setRightPanelCollapsed(true);
}
</script>

<template>
  <aside
    v-if="!rightPanelCollapsed"
    class="right-panel"
    aria-label="Right panel"
  >
    <RightPanelTabs
      :tabs="boardTabs"
      :active-tab="activeTabId"
      @select="handleTabSelect"
      @collapse="handleCollapse"
    />

    <div class="right-content">
      <div class="right-content__panel">
        <BoardSummaryPanel v-if="activeTabId === 'summary'" />
        <BoardActivityPanel v-else-if="activeTabId === 'activity'" />
      </div>
    </div>
  </aside>
</template>

<style scoped>
.right-panel {
  width: 280px;
  min-width: 280px;
  min-height: 0;
  background: var(--panel-bg);
  border-left: 1px solid var(--border);
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.right-content {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 14px 14px 20px;
}

.right-content__panel {
  display: flex;
  flex-direction: column;
  min-height: 100%;
}
</style>
