<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { storeToRefs } from "pinia";
import { X } from "lucide-vue-next";
import ArtifactsPanel from "@/components/session/ArtifactsPanel.vue";
import ArtifactViewerToolbar from "@/components/session/ArtifactViewerToolbar.vue";
import AnnotationPopover from "@/components/annotations/AnnotationPopover.vue";
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
import { useVisualPanel } from "@/composables/use-visual-panel";
import { useAnnotation } from "@/composables/use-annotation";
import { useSendPrompt } from "@/composables/use-send-prompt";
import { useDraftState } from "@/composables/use-draft-state";
import { useSessionsStore } from "@/stores/sessions";
import { useSidebarStore } from "@/stores/sidebar";
import { getVisualRenderer } from "@/lib/visual-renderer-registry";
import { formatAnnotationPrompt } from "@/lib/format-annotation-prompt";
import { extractAnchorText } from "@/lib/annotation-types";
import type { AnnotationAnchor } from "@/lib/annotation-types";

interface Props {
  width?: number;
}

const props = withDefaults(defineProps<Props>(), {
  width: 360,
});

const sidebarStore = useSidebarStore();
const sessionsStore = useSessionsStore();
const sessionDiffsContext = useSessionDiffsContext();
const { visualPayload, clearVisual } = useVisualPanel();

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

// Auto-expand and switch to Visual tab when visual payload is set.
watch(
  visualPayload,
  (next, prev) => {
    if (next && !prev) {
      sidebarStore.setRightPanelCollapsed(false);
      activeTabId.value = "visual";
    }
  },
  { flush: "post" },
);

// --- Tabs ---
const rightPanelTabs = computed(() => {
  const tabs = [
    {
      id: "artifacts",
      label: "Artifacts",
    },
    {
      id: "info",
      label: "Info",
    },
  ] as const;

  if (visualPayload.value) {
    return [
      {
        id: "visual",
        label: "Visual",
      },
      ...tabs,
    ] as const;
  }

  return tabs;
});

type RightPanelTabId = "artifacts" | "info" | "visual";

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
  if (tabId === "artifacts" || tabId === "info" || tabId === "visual") {
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

const visualRenderer = computed(() => {
  if (!visualPayload.value) return null;
  return getVisualRenderer(visualPayload.value.$type);
});

function handleCloseVisual(): void {
  clearVisual();
  activeTabId.value = "artifacts";
}

// --- Annotation flow ---
// We need to initialize these composables with the active session ID
// Since the session ID can change, we'll handle the case where there's no active session
const currentSessionId = computed(() => activeSessionId.value ?? "");

const {
  activeAnchor,
  isPopoverOpen,
  popoverPosition,
  openAnnotation,
  closeAnnotation,
  submitAnnotation,
} = useAnnotation({
  onSubmit: (formattedText: string) => {
    const sessionId = currentSessionId.value;
    if (!sessionId) return;
    
    // Get the composables for the current session
    const { sendPrompt } = useSendPrompt(sessionId);
    const { setText } = useDraftState(sessionId, {
      agentId: "",
      modelId: "",
    });
    
    // Format the annotation prompt with file path if available
    const filePath = visualPayload.value?.sourceFilePath ?? "";
    const anchorText = activeAnchor.value ? extractAnchorText(activeAnchor.value) : "";
    const prompt = formatAnnotationPrompt(filePath, anchorText, formattedText);
    
    // Set the draft text and send
    setText(prompt);
    sendPrompt();
  },
});

function handleAnnotate(anchor: AnnotationAnchor, position: { x: number; y: number }): void {
  openAnnotation(anchor, position);
}

function handleAnnotationSend(text: string): void {
  submitAnnotation(text);
}

function handleAnnotationCancel(): void {
  closeAnnotation();
}

// Check if the current visual renderer is the MarkdownRenderer
const isMarkdownRenderer = computed(() => {
  return visualPayload.value?.$type === "markdown";
});
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

    <div class="right-content" :class="{ 'right-content--visual': activeTabId === 'visual' && visualPayload && visualRenderer }">
      <div class="right-content__panel">
        <section
          v-if="activeTabId === 'visual' && visualPayload && visualRenderer"
          class="visual-panel"
        >
          <ArtifactViewerToolbar v-if="visualPayload.sourceFilePath" />
          <div v-else class="visual-panel__header">
            <h2 class="visual-panel__title">
              {{ visualPayload.title ?? 'Visual Content' }}
            </h2>
            <button
              class="visual-panel__close"
              data-testid="visual-panel-close"
              @click="handleCloseVisual"
            >
              <X class="visual-panel__close-icon" />
            </button>
          </div>
          <div class="visual-panel__content">
            <component
              :is="visualRenderer"
              :content="visualPayload.content"
              :annotatable="isMarkdownRenderer"
              @annotate="handleAnnotate"
            />
          </div>
        </section>

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

    <!-- Annotation Popover -->
    <Teleport to="body">
      <AnnotationPopover
        v-if="isPopoverOpen && activeAnchor"
        :x="popoverPosition.x"
        :y="popoverPosition.y"
        :anchor-text="extractAnchorText(activeAnchor)"
        @send="handleAnnotationSend"
        @cancel="handleAnnotationCancel"
      />
    </Teleport>
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
  display: flex;
  flex-direction: column;
}

.right-content--visual {
  overflow: hidden;
}

.right-content__panel {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-height: 0;
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

.visual-panel {
  display: flex;
  flex-direction: column;
  gap: 12px;
  height: 100%;
}

.visual-panel__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding-bottom: 8px;
  border-bottom: 1px solid var(--border);
}

.visual-panel__title {
  margin: 0;
  font-size: 14px;
  font-weight: 600;
  color: var(--text);
}

.visual-panel__close {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  padding: 0;
  border: 1px solid var(--border);
  border-radius: 0;
  background: var(--surface, #fff);
  color: var(--muted);
  cursor: pointer;
  transition: background var(--transition), color var(--transition);
}

.visual-panel__close:hover {
  background: var(--bg, rgba(0, 0, 0, 0.04));
  color: var(--text);
}

.visual-panel__close-icon {
  width: 14px;
  height: 14px;
}

.visual-panel__content {
  flex: 1;
  min-height: 0;
  overflow: hidden;
}
</style>
