<script setup lang="ts">
import { computed, watch } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { storeToRefs } from "pinia";
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
import { useSessionTodos } from "@/composables/use-session-todos";
import { useSessionDiffsContext } from "@/composables/use-session-diffs-context";
import { useSessionsStore } from "@/stores/sessions";
import { useSidebarStore } from "@/stores/sidebar";

const sidebarStore = useSidebarStore();
const sessionsStore = useSessionsStore();
const router = useRouter();
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

async function setViewMode(viewMode: "chat" | "files-changed"): Promise<void> {
  if (!activeSessionId.value) {
    return;
  }

  await router.navigate({
    to: "/sessions/$id",
    params: { id: activeSessionId.value },
    search: (previous) => ({
      instanceId: previous.instanceId,
      parentSessionId: previous.parentSessionId,
      view: viewMode === "files-changed" ? "files" : undefined,
    }),
    replace: true,
  });
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
          :set-view-mode="setViewMode"
          :open-diffs-tray="openDiffsTray"
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
