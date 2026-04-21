<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { storeToRefs } from "pinia";
import BoardSummaryPanel from "@/components/board/BoardSummaryPanel.vue";
import RightPanelTabs from "@/components/layout/RightPanelTabs.vue";
import BoardActivityPanel from "@/components/board/BoardActivityPanel.vue";
import SessionDetailPanel from "@/components/session/SessionDetailPanel.vue";
import { useSessionsStore } from "@/stores/sessions";
import { useSidebarStore } from "@/stores/sidebar";

type RightPanelMode = "session" | "board";
type RightPanelTabId = "session" | "summary" | "activity";

interface RightPanelTabDefinition {
  id: RightPanelTabId;
  label: string;
  eyebrow: string;
  title: string;
  description: string;
  items: readonly string[];
}

const sessionTabs = [
  {
    id: "session",
    label: "Session",
    eyebrow: "Session",
    title: "Session Details",
    description: "Selected session context, metadata, and quick actions will appear here.",
    items: [
      "Session status, runtime, and ownership details will be surfaced in this panel.",
      "Recent notes and linked project context will stay visible while you work in the main view.",
      "Task-level controls, labels, and session-specific actions will land here next.",
      "This area is intentionally scrollable so longer session detail views stay usable.",
    ],
  },
] as const satisfies readonly RightPanelTabDefinition[];

const boardTabs = [
  {
    id: "summary",
    label: "Summary",
    eyebrow: "Board",
    title: "Board Summary",
    description: "High-level board metrics, rollups, and status summaries will appear here.",
    items: [
      "Board-level progress summaries will help track throughput across all sessions.",
      "Aggregate counts, priorities, and milestone indicators will be presented in this tab.",
      "Cross-session insights and overview notes will surface here for quick scanning.",
      "This panel scrolls independently so summary content can grow without affecting layout.",
    ],
  },
  {
    id: "activity",
    label: "Activity",
    eyebrow: "Board",
    title: "Recent Activity",
    description: "Recent board updates, transitions, and timeline events will appear here.",
    items: [
      "Status changes and notable updates across the board will be listed chronologically.",
      "Activity entries can later expand with actors, timestamps, and linked work items.",
      "This tab is intended for a live event stream and history browsing.",
      "Independent scrolling keeps activity readable even when the feed becomes long.",
    ],
  },
] as const satisfies readonly RightPanelTabDefinition[];

const sidebarStore = useSidebarStore();
const sessionsStore = useSessionsStore();

const { activeRail } = storeToRefs(sidebarStore);
const { sessions, activeSessionId } = storeToRefs(sessionsStore);

const selectedSession = computed(() => {
  return sessions.value.find((session) => session.session.id === activeSessionId.value) ?? null;
});

const hasSelectedSession = computed(() => activeSessionId.value !== null);

const panelMode = computed<RightPanelMode>(() => {
  if (hasSelectedSession.value || activeRail.value === "sessions") {
    return "session";
  }

  if (activeRail.value === "board") {
    return "board";
  }

  return "session";
});

const availableTabs = computed<readonly RightPanelTabDefinition[]>(() => {
  if (panelMode.value === "board") {
    return boardTabs;
  }

  return sessionTabs;
});

const activeTabId = shallowRef<RightPanelTabId>(sessionTabs[0].id);

watch(
  availableTabs,
  (tabs) => {
    if (tabs.some((tab) => tab.id === activeTabId.value)) {
      return;
    }

    activeTabId.value = tabs[0].id;
  },
  { immediate: true },
);

const activeTab = computed<RightPanelTabDefinition>(() => {
  const baseTab = availableTabs.value.find((tab) => tab.id === activeTabId.value)
    ?? (panelMode.value === "board" ? boardTabs[0] : sessionTabs[0]);

  if (panelMode.value !== "session" || !selectedSession.value) {
    return baseTab;
  }

  const statusLabel = getStatusLabel(selectedSession.value.sessionStatus);
  const projectLabel = selectedSession.value.projectName ?? "Ungrouped";

  if (baseTab.id === "session") {
    return {
      ...baseTab,
      eyebrow: projectLabel,
      title: selectedSession.value.session.title,
      description: `${statusLabel} session in ${projectLabel}. Details and quick actions for the selected session appear here.`,
      items: [
        `Session ID: ${selectedSession.value.session.id}`,
        `Project: ${projectLabel}`,
        `Current status: ${statusLabel}`,
        "This panel will expand with metadata, recent activity, and quick actions.",
      ],
    };
  }

  return baseTab;
});

const shouldRenderSessionDetailPanel = computed(() => {
  return panelMode.value === "session" && activeTab.value.id === "session";
});

const shouldRenderPanelIntro = computed(() => {
  return !shouldRenderSessionDetailPanel.value;
});

const shouldRenderBoardSummaryPanel = computed(() => {
  return panelMode.value === "board" && activeTab.value.id === "summary";
});

const shouldRenderBoardActivityPanel = computed(() => {
  return panelMode.value === "board" && activeTab.value.id === "activity";
});

const activeContentKey = computed(() => {
  const sessionKey = selectedSession.value?.session.id ?? "none";

  return `${panelMode.value}-${activeTab.value.id}-${sessionKey}`;
});

function getStatusLabel(status: "active" | "idle" | "stopped" | "completed" | "disconnected" | "error" | "waiting_input"): string {
  switch (status) {
    case "completed":
      return "Complete";
    case "idle":
      return "Idle";
    case "stopped":
    case "disconnected":
      return "Stopped";
    case "error":
      return "Error";
    case "waiting_input":
      return "Waiting for input";
    case "active":
    default:
      return "Running";
  }
}

function handleTabSelect(tabId: string): void {
  const selectedTab = availableTabs.value.find((tab) => tab.id === tabId);

  if (selectedTab) {
    activeTabId.value = selectedTab.id;
  }
}

function handleCollapse(): void {
  sidebarStore.setRightPanelCollapsed(true);
}
</script>

<template>
  <aside
    class="right-panel"
    aria-label="Right panel"
  >
    <RightPanelTabs
      :tabs="availableTabs"
      :active-tab="activeTab.id"
      @select="handleTabSelect"
      @collapse="handleCollapse"
    />

    <div class="right-content">
      <Transition
        name="panel-swap"
        mode="out-in"
      >
        <div
          :key="activeContentKey"
          class="right-content__panel"
        >
          <section
            v-if="shouldRenderPanelIntro"
            class="right-section"
          >
            <p class="right-section__eyebrow">
              {{ activeTab.eyebrow }}
            </p>
            <h2 class="right-section__title">
              {{ activeTab.title }}
            </h2>
            <p class="right-section__description">
              {{ activeTab.description }}
            </p>
          </section>

          <SessionDetailPanel
            v-if="shouldRenderSessionDetailPanel"
            :session="selectedSession"
          />

          <BoardSummaryPanel v-else-if="shouldRenderBoardSummaryPanel" />

          <BoardActivityPanel v-else-if="shouldRenderBoardActivityPanel" />

          <section
            v-else
            class="right-placeholder-list"
            aria-label="Panel placeholder content"
          >
            <article
              v-for="(item, index) in activeTab.items"
              :key="`${activeTab.id}-${index}`"
              class="right-placeholder-card"
            >
              <h3 class="right-placeholder-card__title">
                {{ activeTab.label }} {{ index + 1 }}
              </h3>
              <p class="right-placeholder-card__body">
                {{ item }}
              </p>
            </article>
          </section>
        </div>
      </Transition>
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

.right-section {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin-bottom: 16px;
}

.right-section__eyebrow {
  margin: 0;
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.05em;
  text-transform: uppercase;
  color: var(--muted);
}

.right-section__title {
  margin: 0;
  font-size: 18px;
  font-weight: 600;
  color: var(--text);
}

.right-section__description {
  margin: 0;
  font-size: 13px;
  line-height: 1.5;
  color: var(--muted);
}

.right-placeholder-list {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.right-placeholder-card {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 12px;
  border: 1px solid var(--border);
  border-radius: 10px;
  background: rgba(255, 255, 255, 0.02);
}

.right-placeholder-card__title {
  margin: 0;
  font-size: 12px;
  font-weight: 600;
  color: var(--text);
}

.right-placeholder-card__body {
  margin: 0;
  font-size: 12px;
  line-height: 1.5;
  color: var(--muted);
}
</style>
