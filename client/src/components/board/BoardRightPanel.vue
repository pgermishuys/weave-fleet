<script setup lang="ts">
import { shallowRef } from "vue";
import { storeToRefs } from "pinia";
import BoardActivityPanel from "@/components/board/BoardActivityPanel.vue";
import BoardSummaryPanel from "@/components/board/BoardSummaryPanel.vue";
import RightPanelTabs from "@/components/layout/RightPanelTabs.vue";
import { useSidebarMobile } from "@/composables/use-sidebar-mobile";
import { useSidebarStore } from "@/stores/sidebar";

interface Props {
  width?: number;
  /** The panel fills a sheet over the page (phones, narrow windows), and collapsing closes it. */
  inSheet?: boolean;
}

const props = withDefaults(defineProps<Props>(), {
  width: 360,
  inSheet: false,
});

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

const { hideRightPanel } = useSidebarMobile();
</script>

<template>
  <aside
    v-if="!rightPanelCollapsed || props.inSheet"
    class="right-panel"
    :class="{ 'right-panel--sheet': props.inSheet }"
    :style="props.inSheet ? undefined : { width: `${props.width}px`, minWidth: '280px' }"
    aria-label="Right panel"
  >
    <RightPanelTabs
      :tabs="boardTabs"
      :active-tab="activeTabId"
      @select="handleTabSelect"
      @collapse="hideRightPanel"
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
  min-height: 0;
  background: transparent;
  border-left: 1px solid var(--border);
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.right-panel--sheet {
  flex: 1;
  width: 100%;
  border-left: 0;
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
