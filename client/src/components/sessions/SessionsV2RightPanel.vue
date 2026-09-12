<script setup lang="ts">
import { computed, onBeforeUnmount, provide, ref, watch } from "vue";
import { storeToRefs } from "pinia";
import { PanelRightClose } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import AnnotationPopover from "@/components/annotations/AnnotationPopover.vue";
import CanvasHost from "@/components/canvas/CanvasHost.vue";
import CollapsedRightRail from "@/components/layout/CollapsedRightRail.vue";
import SessionMetadataHeader from "@/components/session/SessionMetadataHeader.vue";
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
import { useAnnotation } from "@/composables/use-annotation";
import { useSendPrompt } from "@/composables/use-send-prompt";
import { useDraftState } from "@/composables/use-draft-state";
import { provideCanvasAnnotate } from "@/composables/use-canvas-annotation";
import { useDiffs } from "@/composables/use-diffs";
import { useServerCanvases } from "@/composables/use-server-canvases";
import { useCanvasesStore } from "@/stores/canvases";
import { useSessionsStore } from "@/stores/sessions";
import { useSidebarStore } from "@/stores/sidebar";
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
const canvasesStore = useCanvasesStore();

const { rightPanelCollapsed } = storeToRefs(sidebarStore);
const { sessions, activeSessionId } = storeToRefs(sessionsStore);

// Create shared diffs instance and fetch on mount
const sharedDiffs = useDiffs(activeSessionId);
provide('sharedDiffs', sharedDiffs);

// Watch session ID and fetch diffs when it changes to a non-null value
watch(
  activeSessionId,
  (sessionId) => {
    if (sessionId) {
      void sharedDiffs.fetchDiffs();
    }
  },
  { immediate: true },
);

// Server canvases stay in sync while the panel is collapsed, so they're current when it opens.
useServerCanvases(activeSessionId);

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
const { todos } = useSessionTodos(
  computed(() => activeSessionId.value ?? ""),
);

function handleExpand(): void {
  sidebarStore.setRightPanelCollapsed(false);
}

function handleCollapse(): void {
  sidebarStore.setRightPanelCollapsed(true);
}

// Animate the width only when Widen is toggled, never while the gutter is dragged.
const isResizingForWiden = ref(false);
let widenTimer: ReturnType<typeof setTimeout> | undefined;

watch(
  () => canvasesStore.widened,
  () => {
    isResizingForWiden.value = true;
    clearTimeout(widenTimer);
    widenTimer = setTimeout(() => {
      isResizingForWiden.value = false;
    }, 240);
  },
);

onBeforeUnmount(() => clearTimeout(widenTimer));

// --- Annotation flow: any canvas can ask the agent about selected text ---
const annotationFilePath = ref("");

const {
  activeAnchor,
  isPopoverOpen,
  popoverPosition,
  openAnnotation,
  closeAnnotation,
  submitAnnotation,
} = useAnnotation({
  onSubmit: (formattedText: string) => {
    const sessionId = activeSessionId.value;
    if (!sessionId) return;

    const { sendPrompt } = useSendPrompt(sessionId);
    const { setText } = useDraftState(sessionId, {
      agentId: "",
      modelId: "",
    });

    const anchorText = activeAnchor.value ? extractAnchorText(activeAnchor.value) : "";
    setText(formatAnnotationPrompt(annotationFilePath.value, anchorText, formattedText));
    sendPrompt();
  },
});

provideCanvasAnnotate((anchor: AnnotationAnchor, position: { x: number; y: number }, sourceFilePath: string) => {
  annotationFilePath.value = sourceFilePath;
  openAnnotation(anchor, position);
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
    :class="{ 'right-panel--widening': isResizingForWiden }"
    :style="{ width: `${props.width}px`, minWidth: '280px' }"
    aria-label="Right panel"
  >
    <CanvasHost :session-id="activeSessionId ?? ''">
      <template #header-actions>
        <Button
          variant="toolbar-icon"
          size="toolbar"
          class="right-panel__collapse"
          aria-label="Collapse right panel"
          title="Collapse right panel"
          @click="handleCollapse"
        >
          <PanelRightClose />
        </Button>
      </template>
      <template #below-header>
        <SessionMetadataHeader
          class="right-panel__meta"
          :session="selectedSession"
        />
      </template>
    </CanvasHost>

    <Teleport to="body">
      <AnnotationPopover
        v-if="isPopoverOpen && activeAnchor"
        :x="popoverPosition.x"
        :y="popoverPosition.y"
        :anchor-text="extractAnchorText(activeAnchor)"
        @send="submitAnnotation"
        @cancel="closeAnnotation"
      />
    </Teleport>
  </aside>
</template>

<style scoped>
.right-panel {
  position: relative;
  min-height: 0;
  background: transparent;
  border-left: 1px solid var(--border);
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.right-panel--widening {
  transition: width 200ms cubic-bezier(0.2, 0.8, 0.2, 1), min-width 200ms cubic-bezier(0.2, 0.8, 0.2, 1);
}

.right-panel__meta:empty {
  display: none;
}

.right-panel__meta {
  padding: 0 12px;
}

@media (prefers-reduced-motion: reduce) {
  .right-panel--widening {
    transition: none;
  }
}
</style>
