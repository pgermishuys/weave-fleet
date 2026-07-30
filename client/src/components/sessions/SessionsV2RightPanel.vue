<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { storeToRefs } from "pinia";
import ArtifactsPanel from "@/components/session/ArtifactsPanel.vue";
import CollapsedRightRail from "@/components/layout/CollapsedRightRail.vue";
import RightPanelTabs from "@/components/layout/RightPanelTabs.vue";
import SessionDetailPanel from "@/components/session/SessionDetailPanel.vue";
import {
  useAbortSession,
  useArchiveSession,
  useDeleteSession,
  useRenameSession,
  useResumeSession,
  useTerminateSession,
} from "@/composables/use-session-actions";
import { provideSessionDetailContext } from "@/composables/use-session-detail-context";
import { useSessionDiffsContext } from "@/composables/use-session-diffs-context";
import { useSessionTodos } from "@/composables/use-session-todos";
import { useSessionsStore } from "@/stores/sessions";
import { useSidebarStore } from "@/stores/sidebar";

interface Props {
  width?: number;
}

const props = withDefaults(defineProps<Props>(), {
  width: 360,
});

const sidebarStore = useSidebarStore();
const sessionsStore = useSessionsStore();
const sessionDiffsContext = useSessionDiffsContext();

const { rightPanelCollapsed } = storeToRefs(sidebarStore);
const { sessions, activeSessionId } = storeToRefs(sessionsStore);

const selectedSession = computed(() =>
  sessions.value.find((s) => s.session.id === activeSessionId.value) ?? null,
);

// --- Action composables (V2) ---
const abort = useAbortSession();
const archive = useArchiveSession();
const del = useDeleteSession();
const rename = useRenameSession();
const resume = useResumeSession();
const terminate = useTerminateSession();
provideSessionDetailContext({
  apiBasePath: "/api/sessions",
  sessionRoutePath: "/sessions/$id",
  supportsFork: true,
  supportsArchive: true,
  actionsLayout: "card",
  patchSession: (id, patch) => sessionsStore.patchSession(id, patch),
  abort,
  archive,
  delete: del,
  rename,
  resume,
  terminate,
});

// --- Collapsed rail: todos ---
const activeInstanceId = computed(() => selectedSession.value?.instanceId ?? "");
const { todos } = useSessionTodos(
  computed(() => activeSessionId.value ?? ""),
  activeInstanceId,
);

// Auto-expand when a session is first selected.
watch(
  activeSessionId,
  (next, prev) => {
    if (next && !prev) {
      sidebarStore.setRightPanelCollapsed(false);
    }
  },
  { flush: "post" },
);

// Auto-expand when a new todo arrives.
watch(
  [activeSessionId, () => todos.value.length] as const,
  ([nextSessionId, nextCount], [prevSessionId, prevCount]) => {
    if (!nextSessionId) {
      return;
    }
    if (nextSessionId !== prevSessionId) {
      return;
    }
    if (nextCount > (prevCount ?? 0)) {
      sidebarStore.setRightPanelCollapsed(false);
    }
  },
  { flush: "post", immediate: true },
);

// --- Tabs ---
const rightPanelTabs = [
  {
    id: "artifacts",
    label: "Artifacts",
  },
  {
    id: "info",
    label: "Info",
  },
] as const;

type RightPanelTabId = (typeof rightPanelTabs)[number]["id"];

const activeTabId = shallowRef<RightPanelTabId>("artifacts");

const sessionTab = {
  id: "session",
  label: "Session",
  eyebrow: "Session",
  title: "Session Details",
  description: "Selected session context, metadata, and quick actions will appear here.",
} as const;

const activeTab = computed(() => {
  if (!selectedSession.value) {
    return sessionTab;
  }

  const statusLabel = getStatusLabel(selectedSession.value.sessionStatus);
  const projectLabel = selectedSession.value.projectName ?? "Ungrouped";

  return {
    ...sessionTab,
    eyebrow: projectLabel,
    title: selectedSession.value.session.title,
    description: `${statusLabel} session in ${projectLabel}. Details and quick actions for the selected session appear here.`,
  };
});

function handleTabSelect(tabId: string): void {
  if (tabId === "artifacts" || tabId === "info") {
    activeTabId.value = tabId;
  }
}

function getStatusLabel(status: string): string {
  switch (status) {
    case "completed": return "Complete";
    case "idle": return "Idle";
    case "stopped":
    case "disconnected": return "Stopped";
    case "error": return "Error";
    case "waiting_input": return "Waiting for input";
    default: return "Running";
  }
}

function handleCollapse(): void {
  sidebarStore.setRightPanelCollapsed(true);
}

function handleExpand(): void {
  sidebarStore.setRightPanelCollapsed(false);
}

function openDiffsTray(): void {
  const context = sessionDiffsContext.value;
  if (!context?.openDiffsTray) {
    return;
  }

  context.openDiffsTray();
}
</script>

<template>
  <CollapsedRightRail
    v-if="rightPanelCollapsed"
    :todos="todos"
    @expand="handleExpand"
  />

  <aside
    v-else
    class="right-panel"
    :style="{ width: `${props.width}px`, minWidth: '280px' }"
    aria-label="Right panel"
  >
    <RightPanelTabs
      :tabs="rightPanelTabs"
      :active-tab="activeTabId"
      @select="handleTabSelect"
      @collapse="handleCollapse"
    />

    <div class="right-content">
      <div class="right-content__panel">
        <ArtifactsPanel v-if="activeTabId === 'artifacts'" />

        <template v-else-if="activeTabId === 'info'">
          <section
            v-if="!selectedSession"
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
            v-else
            :session="selectedSession"
            :open-diffs-tray="openDiffsTray"
          />
        </template>
      </div>
    </div>
  </aside>
</template>

<style scoped>
.right-panel {
  position: relative;
  min-height: 0;
  background: var(--panel-bg);
  border: 1px solid var(--border);
  border-radius: var(--radius-panel);
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.right-content {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 10px 10px 16px;
}

.right-content__panel {
  display: flex;
  flex-direction: column;
  min-height: 100%;
}

.right-section {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin-bottom: 12px;
}

.right-section__eyebrow {
  margin: 0;
  font-size: 9px;
  font-weight: 600;
  letter-spacing: 0.05em;
  text-transform: uppercase;
  color: var(--muted);
}

.right-section__title {
  margin: 0;
  font-size: 16px;
  font-weight: 600;
  color: var(--text);
}

.right-section__description {
  margin: 0;
  font-size: 11px;
  line-height: 1.4;
  color: var(--muted);
}
</style>
