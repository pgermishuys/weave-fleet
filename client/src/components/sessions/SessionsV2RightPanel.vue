<script setup lang="ts">
import { computed, onBeforeUnmount, provide, ref, watch } from "vue";
import { storeToRefs } from "pinia";
import { PanelRightClose, X } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import AnnotationPopover from "@/components/annotations/AnnotationPopover.vue";
import CanvasHost from "@/components/canvas/CanvasHost.vue";
import CollapsedRightRail from "@/components/layout/CollapsedRightRail.vue";
import SessionMetadataHeader from "@/components/session/SessionMetadataHeader.vue";
import { useSessionProgress } from "@/composables/use-session-progress";
import { useAnnotation } from "@/composables/use-annotation";
import { useSendPrompt } from "@/composables/use-send-prompt";
import { useSidebarMobile } from "@/composables/use-sidebar-mobile";
import { useDraftState } from "@/composables/use-draft-state";
import { provideCanvasAnnotate } from "@/composables/use-canvas-annotation";
import { useDiffs } from "@/composables/use-diffs";
import { useServerCanvases } from "@/composables/use-server-canvases";
import { useCanvasesStore } from "@/stores/canvases";
import { useSessionsStore } from "@/stores/sessions";
import { useSidebarStore } from "@/stores/sidebar";
import { useSmartLinksStore } from "@/stores/smart-links";
import type { CanvasTabBadge } from "@/lib/canvas-registry";
import { needsAttention } from "@/lib/smart-links";
import { formatAnnotationPrompt } from "@/lib/format-annotation-prompt";
import { extractAnchorText } from "@/lib/annotation-types";
import type { AnnotationAnchor } from "@/lib/annotation-types";

interface Props {
  width?: number;
  /** The panel fills a sheet over the conversation (phones, narrow windows): no rail, no Widen, and collapsing closes it. */
  inSheet?: boolean;
}

const props = withDefaults(defineProps<Props>(), {
  width: 360,
  inSheet: false,
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

// --- Context tab: added (without focus) the first time the session has something attached ---
const smartLinksStore = useSmartLinksStore();

watch(
  activeSessionId,
  (sessionId) => {
    if (sessionId) void smartLinksStore.ensureLoaded(sessionId);
  },
  { immediate: true },
);

const contextLinks = computed(() =>
  activeSessionId.value ? smartLinksStore.visibleLinks(activeSessionId.value) : [],
);

const hasContext = computed(() =>
  contextLinks.value.length > 0 || selectedSession.value?.origin?.sourceType === "automation",
);

watch(
  [activeSessionId, hasContext],
  ([sessionId, has]) => {
    if (sessionId && has) canvasesStore.introduce(sessionId, "context");
  },
  { immediate: true },
);

// --- Progress tab: added (without focus) the first time the session has a todo list or a plan ---
const { progress } = useSessionProgress(computed(() => activeSessionId.value ?? ""));
const hasProgress = computed(() => (progress.value?.total ?? 0) > 0);

watch(
  [activeSessionId, hasProgress],
  ([sessionId, has]) => {
    if (sessionId && has) canvasesStore.introduce(sessionId, "progress");
  },
  { immediate: true },
);

const tabBadges = computed<Record<string, CanvasTabBadge>>(() => {
  const attention = contextLinks.value.some(needsAttention);
  const count = contextLinks.value.length;
  const done = progress.value?.done ?? 0;
  const total = progress.value?.total ?? 0;
  return {
    context: {
      attention,
      count,
      label: attention ? "A linked pull request needs attention" : `${count} linked`,
    },
    progress: {
      count: total > 0 ? `${done}/${total}` : undefined,
      label: `${done} of ${total} done`,
    },
  };
});

const { showRightPanel, hideRightPanel } = useSidebarMobile();

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
    v-if="rightPanelCollapsed && !props.inSheet"
    :done="progress?.done ?? 0"
    :total="progress?.total ?? 0"
    @expand="showRightPanel"
  />

  <aside
    v-else
    class="right-panel"
    :class="{ 'right-panel--widening': isResizingForWiden, 'right-panel--sheet': props.inSheet }"
    :style="props.inSheet ? undefined : { width: `${props.width}px`, minWidth: '280px' }"
    aria-label="Right panel"
  >
    <CanvasHost
      :session-id="activeSessionId ?? ''"
      :tab-badges="tabBadges"
      :widenable="!props.inSheet"
    >
      <template #header-actions>
        <Button
          variant="toolbar-icon"
          size="toolbar"
          class="right-panel__collapse"
          :aria-label="props.inSheet ? 'Close right panel' : 'Collapse right panel'"
          :title="props.inSheet ? 'Close right panel' : 'Collapse right panel'"
          @click="hideRightPanel"
        >
          <X v-if="props.inSheet" />
          <PanelRightClose v-else />
        </Button>
      </template>
      <template #below-header>
        <SessionMetadataHeader
          class="right-panel__meta"
          :session-id="activeSessionId ?? ''"
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

.right-panel--sheet {
  flex: 1;
  width: 100%;
  border-left: 0;
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
