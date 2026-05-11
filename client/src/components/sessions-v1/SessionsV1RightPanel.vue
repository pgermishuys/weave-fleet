<script setup lang="ts">
import { computed, watch } from "vue";
import { storeToRefs } from "pinia";
import CollapsedRightRail from "@/components/layout/CollapsedRightRail.vue";
import RightPanelTabs from "@/components/layout/RightPanelTabs.vue";
import SessionDetailPanel from "@/components/session/SessionDetailPanel.vue";
import {
  useAbortSessionV1,
  useArchiveSessionV1,
  useDeleteSessionV1,
  useRenameSessionV1,
  useResumeSessionV1,
  useTerminateSessionV1,
  useUnarchiveSessionV1,
} from "@/composables/use-session-actions-v1";
import { provideSessionDetailContext } from "@/composables/use-session-detail-context";
import { useSessionTodos } from "@/composables/use-session-todos";
import { useSessionsV1Store } from "@/stores/sessions-v1";
import { useSidebarStore } from "@/stores/sidebar";

const sidebarStore = useSidebarStore();
const sessionsStore = useSessionsV1Store();

const { rightPanelCollapsed } = storeToRefs(sidebarStore);
const { sessions, activeSessionId } = storeToRefs(sessionsStore);

const selectedSession = computed(() =>
  sessions.value.find((s) => s.session.id === activeSessionId.value) ?? null,
);

// --- Action composables (V1) ---
const abort = useAbortSessionV1();
const archive = useArchiveSessionV1();
const del = useDeleteSessionV1();
const rename = useRenameSessionV1();
const resume = useResumeSessionV1();
const terminate = useTerminateSessionV1();
const unarchive = useUnarchiveSessionV1();

provideSessionDetailContext({
  apiBasePath: "/api/sessions-v1",
  sessionRoutePath: "/sessions-v1/$id",
  supportsFork: false,
  supportsArchive: false,
  actionsLayout: "toolbar",
  patchSession: (id, patch) => sessionsStore.patchSession(id, patch),
  abort,
  archive,
  delete: del,
  rename,
  resume,
  terminate,
  unarchive,
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

  return {
    ...sessionTab,
    eyebrow: "Workspace",
    title: selectedSession.value.session.title,
    description: `${statusLabel} session. Details and quick actions for the selected session appear here.`,
  };
});

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
    aria-label="Right panel"
  >
    <RightPanelTabs
      :tabs="[sessionTab]"
      :active-tab="sessionTab.id"
      @collapse="handleCollapse"
    />

    <div class="right-content">
      <div class="right-content__panel">
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
        />
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

.right-section {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin-bottom: 16px;
}

.right-section__eyebrow {
  margin: 0;
  font-size: 10px;
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
  font-size: 12px;
  line-height: 1.5;
  color: var(--muted);
}
</style>
