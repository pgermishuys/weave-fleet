<script setup lang="ts">
import { computed, ref, shallowRef, watch } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { Archive, GitFork, Loader2, OctagonX, Pencil, RotateCcw, Square, Trash2, FileText, ChevronDown, ChevronRight } from "lucide-vue-next";
import ConfirmDeleteSessionDialog from "@/components/sessions/ConfirmDeleteSessionDialog.vue";
import ForkSessionDialog from "@/components/session/ForkSessionDialog.vue";
import SmartLinkItem from "@/plugins/builtin/smart-links/SmartLinkItem.vue";
import TodoListView from "@/components/session/TodoListView.vue";
import { useSessionTodos } from "@/composables/use-session-todos";
import { useSessionDetailContext } from "@/composables/use-session-detail-context";
import { useVisualPanel } from "@/composables/use-visual-panel";
import { useContentPanelContext } from "@/composables/use-content-panel";
import { apiFetch } from "@/lib/api-client";
import { trackAction } from "@/lib/track-action";
import { getVisualPayloadSyntheticPath } from "@/lib/visual-payload-path";
import type { SessionActionCapabilities, SessionListItem } from "@/api/client";
import { useSmartLinksStore } from "@/stores/smart-links";
import { secondsUntilRefresh, isRefreshing, refreshNow as useSmartLinksRefresh, POLL_INTERVAL_SECONDS } from "@/plugins/builtin/smart-links/composables/use-smart-links";

interface SessionApiDetail {
  id?: string;
  instanceId?: string;
  title?: string;
  status?: string;
  directory?: string;
  workspaceDirectory?: string;
  workspaceDisplayName?: string | null;
  sourceDirectory?: string | null;
  isolationStrategy?: string | null;
  branch?: string | null;
  createdAt?: string;
  stoppedAt?: string | null;
  activityStatus?: string | null;
  lifecycleStatus?: string | null;
  retentionStatus?: string | null;
  harnessType?: string;
  workspaceId?: string;
  projectId?: string | null;
  capabilities?: SessionActionCapabilities;
}

const props = defineProps<{
  session: SessionListItem | null;
}>();

const router = useRouter();
const ctx = useSessionDetailContext();
const smartLinksStore = useSmartLinksStore();
const contentPanel = useContentPanelContext();

const { abortSession, isAborting, error: abortError } = ctx.abort;
const { archiveSession, isArchiving, error: archiveError } = ctx.archive;
const { deleteSession, isDeleting, error: deleteError } = ctx.delete;
const { renameSession, isLoading: isRenaming, error: renameError } = ctx.rename;
const {
  resumeSession,
  isResuming,
  resumingSessionId,
  error: resumeError,
} = ctx.resume;
const { terminateSession, isTerminating, error: terminateError } = ctx.terminate;

// Circular arc countdown for smart links refresh
const ARC_RADIUS = 7;
const ARC_CIRCUMFERENCE = 2 * Math.PI * ARC_RADIUS;
const arcOffset = computed(() =>
  ARC_CIRCUMFERENCE * (1 - secondsUntilRefresh.value / POLL_INTERVAL_SECONDS),
);

async function handleSmartLinksRefreshNow(): Promise<void> {
  await useSmartLinksRefresh();
}

const remoteSessionDetail = ref<SessionApiDetail | null>(null);
const refreshVersion = shallowRef(0);
const isDeleteDialogOpen = shallowRef(false);
const isForkDialogOpen = shallowRef(false);
const isTodosExpanded = shallowRef(false);

const sessionId = computed(() => props.session?.session.id ?? null);
const activeSmartLinks = computed(() => sessionId.value ? smartLinksStore.getActiveLinks(sessionId.value) : []);
const smartLinkPRs = computed(() => activeSmartLinks.value.filter(l => l.resourceType === "pull_request"));
const smartLinkIssues = computed(() => activeSmartLinks.value.filter(l => l.resourceType !== "pull_request"));
const resolvedInstanceId = computed(() => normalizeString(props.session?.instanceId) ?? normalizeString(remoteSessionDetail.value?.instanceId));
const todoSessionId = computed(() => sessionId.value ?? "");
const sessionTitle = computed(() => normalizeString(props.session?.session.title) ?? normalizeString(remoteSessionDetail.value?.title) ?? "Untitled session");
const effectiveSessionStatus = computed(() => props.session?.sessionStatus
  ?? remoteSessionDetail.value?.lifecycleStatus
  ?? remoteSessionDetail.value?.status
  ?? null);
const { todos } = useSessionTodos(todoSessionId);
const completedTodosCount = computed(() => todos.value.filter((t) => t.status === "completed").length);
const todoProgressLabel = computed(() => `${completedTodosCount.value} of ${todos.value.length} todos`);
const effectiveLifecycleStatus = computed(() => normalizeLifecycleStatus(
  props.session?.lifecycleStatus
    ?? remoteSessionDetail.value?.lifecycleStatus
    ?? effectiveSessionStatus.value,
));
const effectiveActivityStatus = computed(() => normalizeActivityStatus(
  props.session?.activityStatus
    ?? remoteSessionDetail.value?.activityStatus
    ?? props.session?.sessionStatus
    ?? remoteSessionDetail.value?.status,
));
const effectiveRetentionStatus = computed(() => normalizeRetentionStatus(
  props.session?.retentionStatus ?? remoteSessionDetail.value?.retentionStatus,
));
const isRunningSession = computed(() => effectiveLifecycleStatus.value === "running");
const isBusySession = computed(() => isActiveActivityStatus(effectiveActivityStatus.value));
const effectiveCapabilities = computed(() => props.session?.capabilities ?? remoteSessionDetail.value?.capabilities);
const fallbackCanAbort = computed(() => isRunningSession.value && isBusySession.value);
const fallbackCanResume = computed(() => {
  switch (effectiveLifecycleStatus.value) {
    case "stopped":
    case "completed":
    case "disconnected":
      return true;
    default:
      return false;
  }
});
const fallbackCanStop = computed(() => isRunningSession.value);
const fallbackCanArchive = computed(() => effectiveRetentionStatus.value !== "archived" && !isRunningSession.value);
const fallbackCanFork = computed(() => ctx.supportsFork);
const fallbackCanDelete = computed(() => true);
const canAbort = computed(() => effectiveCapabilities.value?.canAbort ?? fallbackCanAbort.value);
const canResume = computed(() => effectiveCapabilities.value?.canResume ?? fallbackCanResume.value);
const canStop = computed(() => effectiveCapabilities.value?.canStop ?? fallbackCanStop.value);
const canArchive = computed(() => effectiveCapabilities.value?.canArchive ?? fallbackCanArchive.value);
const canFork = computed(() => effectiveCapabilities.value?.canFork ?? fallbackCanFork.value);
const canDelete = computed(() => effectiveCapabilities.value?.canDelete ?? fallbackCanDelete.value);
const isResumingCurrentSession = computed(() => isResuming.value && resumingSessionId.value === sessionId.value);
const isAnyActionPending = computed(() => isAborting.value
  || isArchiving.value
  || isDeleting.value
  || isRenaming.value
  || isResumingCurrentSession.value
  || isTerminating.value);
const actionErrors = computed(() => [
  abortError.value,
  archiveError.value,
  deleteError.value,
  renameError.value,
  resumeError.value,
  terminateError.value,
].filter((message): message is string => Boolean(message)));

// Artifact chip — reflects the currently mirrored visual artifact for this session, if any.
const visualPanel = computed(() => sessionId.value ? useVisualPanel(sessionId.value) : null);
const artifactPayload = computed(() => visualPanel.value?.visualPayload.value ?? null);
const artifactName = computed(() => {
  const path = artifactPayload.value?.sourceFilePath;
  if (!path) return null;
  const segments = path.split("/");
  return segments[segments.length - 1] || path;
});

function handleArtifactChipActivate(): void {
  const payload = artifactPayload.value;
  const panel = visualPanel.value;

  if (payload) {
    if (panel) {
      panel.showVisual(payload);
    }
    void contentPanel.selectFile(getVisualPayloadSyntheticPath(payload));
    return;
  }

  const name = artifactName.value;
  if (name) {
    void contentPanel.selectFile(`__visual__/${name}`);
  }
}

function handleArtifactChipKeydown(event: KeyboardEvent): void {
  if (event.key === "Enter" || event.key === " " || event.key === "Spacebar") {
    event.preventDefault();
    handleArtifactChipActivate();
  }
}

function toggleTodosExpanded(): void {
  isTodosExpanded.value = !isTodosExpanded.value;
}

watch(
  [sessionId, refreshVersion],
  async ([nextSessionId], _previous, onCleanup) => {
    remoteSessionDetail.value = null;

    if (!nextSessionId) {
      return;
    }

    const controller = new AbortController();
    onCleanup(() => controller.abort());

    try {
      const detailResponse = await apiFetch(`${ctx.apiBasePath}/${encodeURIComponent(nextSessionId)}`, {
        signal: controller.signal,
      });

      if (!detailResponse.ok) {
        throw new Error(`HTTP ${detailResponse.status}`);
      }

      const detail = (await detailResponse.json()) as SessionApiDetail;
      remoteSessionDetail.value = detail;
      trackAction("session.view", nextSessionId);

      // Load persisted smart links so they appear immediately (without waiting for ActivityStream)
      try {
        const linksResponse = await apiFetch(`/api/sessions/${encodeURIComponent(nextSessionId)}/smart-links/all`, {
          signal: controller.signal,
        });
        if (linksResponse.ok) {
          const links = await linksResponse.json();
          smartLinksStore.setLinks(nextSessionId, links);
        }
      } catch {
        // Smart links are non-critical — silently ignore
      }
    } catch (error) {
      if (error instanceof DOMException && error.name === "AbortError") {
        return;
      }
    }
  },
  { immediate: true },
);

async function handleAbort(): Promise<void> {
  if (!sessionId.value || !resolvedInstanceId.value || !canAbort.value) {
    return;
  }

  try {
    await abortSession(sessionId.value);
    refreshPanelData();
  } catch {
    // Error is exposed inline by the composable.
  }
}

async function handleResume(): Promise<void> {
  if (!sessionId.value || !canResume.value) {
    return;
  }

  try {
    ctx.patchSession(sessionId.value, {
      activityStatus: "idle",
      lifecycleStatus: "resuming",
      sessionStatus: "resuming",
    });
    const response = await resumeSession(sessionId.value);
    refreshPanelData();
    await router.navigate({
      to: ctx.sessionRoutePath,
      params: { id: response.session.id },
      search: {
        instanceId: response.instanceId,
        parentSessionId: undefined,
      },
    });
  } catch {
    // Revert to stopped on failure
    if (sessionId.value) {
      ctx.patchSession(sessionId.value, {
        activityStatus: "idle",
        lifecycleStatus: "stopped",
        sessionStatus: "stopped",
      });
    }
  }
}

async function handleStop(): Promise<void> {
  if (!sessionId.value || !resolvedInstanceId.value || !canStop.value) {
    return;
  }

  try {
    await terminateSession(sessionId.value, resolvedInstanceId.value);
    ctx.patchSession(sessionId.value, {
      activityStatus: "idle",
      lifecycleStatus: "stopped",
      sessionStatus: "stopped",
    });
    refreshPanelData();
  } catch {
    // Error is exposed inline by the composable.
  }
}

async function handleFork(): Promise<void> {
  if (!sessionId.value || !canFork.value) {
    return;
  }

  isForkDialogOpen.value = true;
}

async function handleDelete(): Promise<void> {
  if (!sessionId.value || !resolvedInstanceId.value || !canDelete.value) {
    return;
  }

  isDeleteDialogOpen.value = true;
}

async function handleDeleteConfirmed(): Promise<void> {
  if (!sessionId.value || !resolvedInstanceId.value || !canDelete.value) {
    return;
  }

  try {
    await deleteSession(sessionId.value, resolvedInstanceId.value);
    isDeleteDialogOpen.value = false;
    await router.navigate({ to: "/" });
  } catch {
    // Error is exposed inline by the composable.
  }
}

async function handleRename(): Promise<void> {
  if (!sessionId.value) {
    return;
  }

  const proposedTitle = window.prompt("Rename session", sessionTitle.value)?.trim();
  if (!proposedTitle || proposedTitle === sessionTitle.value) {
    return;
  }

  try {
    await renameSession(sessionId.value, proposedTitle, () => {
      if (remoteSessionDetail.value) {
        remoteSessionDetail.value = {
          ...remoteSessionDetail.value,
          title: proposedTitle,
        };
      }
    });
    refreshPanelData();
  } catch {
    // Error is exposed inline by the composable.
  }
}

async function handleArchive(): Promise<void> {
  if (!sessionId.value || !canArchive.value) {
    return;
  }

  try {
    await archiveSession(sessionId.value);
    ctx.patchSession(sessionId.value, {
      retentionStatus: "archived",
    });
    refreshPanelData();
  } catch {
    // Error is exposed inline by the composable.
  }
}

function refreshPanelData(): void {
  refreshVersion.value += 1;
}

function normalizeString(value: string | null | undefined): string | null {
  if (!value) {
    return null;
  }

  const normalized = value.trim();
  return normalized.length > 0 ? normalized : null;
}

function normalizeLifecycleStatus(value: string | null | undefined): "running" | "resuming" | "completed" | "stopped" | "error" | "disconnected" | null {
  switch (value) {
    case "active":
    case "delegating":
    case "idle":
    case "waiting_input":
    case "running":
      return "running";
    case "resuming":
      return "resuming";
    case "complete":
    case "completed":
      return "completed";
    case "stopped":
    case "dead":
      return "stopped";
    case "error":
      return "error";
    case "disconnected":
      return "disconnected";
    default:
      return null;
  }
}

function normalizeActivityStatus(value: string | null | undefined): "busy" | "delegating" | "idle" | "waiting_input" | null {
  switch (value) {
    case "active":
    case "busy":
      return "busy";
    case "delegating":
      return "delegating";
    case "idle":
      return "idle";
    case "waiting_input":
      return "waiting_input";
    default:
      return null;
  }
}

function isActiveActivityStatus(value: string | null | undefined): value is "busy" | "delegating" {
  return value === "busy" || value === "delegating";
}

function normalizeRetentionStatus(value: string | null | undefined): "active" | "archived" {
  return value === "archived" ? "archived" : "active";
}

async function handleDismissSmartLink(linkId: string): Promise<void> {
  const sid = sessionId.value;
  if (!sid) return;
  try {
    const response = await apiFetch(`${ctx.apiBasePath}/${encodeURIComponent(sid)}/smart-links/${encodeURIComponent(linkId)}/dismiss`, {
      method: "PATCH",
    });
    if (response.ok) {
      smartLinksStore.dismissLink(sid, linkId);
    }
  } catch {
    // silently ignore
  }
}
</script>

<template>
  <header
    class="session-metadata-header"
    aria-label="Session metadata"
  >
    <!-- Actions toolbar -->
    <div
      v-if="ctx.actionsLayout === 'toolbar'"
      class="session-action-toolbar"
      role="toolbar"
      aria-label="Session actions"
    >
      <button
        v-if="canAbort"
        type="button"
        data-testid="abort-button"
        class="session-action-toolbar__btn session-action-toolbar__btn--danger"
        :disabled="isAnyActionPending || !sessionId || !resolvedInstanceId"
        title="Abort"
        aria-label="Abort session"
        @click="handleAbort"
      >
        <Loader2
          v-if="isAborting"
          :size="14"
          class="session-action-toolbar__spinner"
          aria-hidden="true"
        />
        <OctagonX
          v-else
          :size="14"
          aria-hidden="true"
        />
      </button>

      <button
        v-if="canResume"
        type="button"
        data-testid="session-resume-button"
        class="session-action-toolbar__btn"
        :disabled="isAnyActionPending || !sessionId"
        title="Resume"
        aria-label="Resume session"
        @click="handleResume"
      >
        <Loader2
          v-if="isResumingCurrentSession"
          :size="14"
          class="session-action-toolbar__spinner"
          aria-hidden="true"
        />
        <RotateCcw
          v-else
          :size="14"
          aria-hidden="true"
        />
      </button>

      <button
        v-if="canStop"
        type="button"
        data-testid="session-stop-button"
        class="session-action-toolbar__btn session-action-toolbar__btn--danger"
        :disabled="isAnyActionPending || !sessionId || !resolvedInstanceId"
        title="Stop"
        aria-label="Stop session"
        @click="handleStop"
      >
        <Loader2
          v-if="isTerminating"
          :size="14"
          class="session-action-toolbar__spinner"
          aria-hidden="true"
        />
        <Square
          v-else
          :size="14"
          aria-hidden="true"
        />
      </button>

      <span class="session-action-toolbar__divider" />

      <button
        v-if="canFork"
        type="button"
        data-testid="session-archived-fork-button"
        class="session-action-toolbar__btn"
        :disabled="isAnyActionPending || !sessionId"
        title="Fork"
        aria-label="Fork session"
        @click="handleFork"
      >
        <GitFork
          :size="14"
          aria-hidden="true"
        />
      </button>

      <button
        v-if="canDelete"
        type="button"
        class="session-action-toolbar__btn"
        :disabled="isAnyActionPending || !sessionId"
        title="Rename"
        aria-label="Rename session"
        @click="handleRename"
      >
        <Loader2
          v-if="isRenaming"
          :size="14"
          class="session-action-toolbar__spinner"
          aria-hidden="true"
        />
        <Pencil
          v-else
          :size="14"
          aria-hidden="true"
        />
      </button>

      <button
        v-if="canDelete"
        type="button"
        data-testid="session-delete-button"
        class="session-action-toolbar__btn session-action-toolbar__btn--danger"
        :disabled="isAnyActionPending || !sessionId || !resolvedInstanceId"
        title="Delete"
        aria-label="Delete session"
        @click="handleDelete"
      >
        <Loader2
          v-if="isDeleting"
          :size="14"
          class="session-action-toolbar__spinner"
          aria-hidden="true"
        />
        <Trash2
          v-else
          :size="14"
          aria-hidden="true"
        />
      </button>

      <button
        v-if="canArchive && ctx.supportsArchive"
        type="button"
        data-testid="session-archive-banner-button"
        class="session-action-toolbar__btn"
        :disabled="isAnyActionPending || !sessionId"
        title="Archive"
        aria-label="Archive session"
        @click="handleArchive"
      >
        <Loader2
          v-if="isArchiving"
          :size="14"
          class="session-action-toolbar__spinner"
          aria-hidden="true"
        />
        <Archive
          v-else
          :size="14"
          aria-hidden="true"
        />
      </button>

      <p
        v-for="message in actionErrors"
        :key="message"
        class="session-action-toolbar__error"
        role="alert"
      >
        {{ message }}
      </p>
    </div>

    <!-- Smart-link / todo / artifact chips row -->
    <div
      v-if="activeSmartLinks.length > 0 || todos.length > 0 || artifactName"
      class="session-meta-chips"
      role="list"
      aria-label="Session links and artifacts"
    >
      <button
        v-if="todos.length > 0"
        type="button"
        class="meta-chip meta-chip--todo"
        role="listitem"
        :aria-label="`${todoProgressLabel}. ${isTodosExpanded ? 'Collapse' : 'Expand'} todo list`"
        :aria-expanded="isTodosExpanded"
        @click="toggleTodosExpanded"
      >
        <component
          :is="isTodosExpanded ? ChevronDown : ChevronRight"
          :size="11"
          aria-hidden="true"
        />
        {{ todoProgressLabel }}
      </button>

      <button
        v-if="artifactName"
        type="button"
        class="meta-chip meta-chip--artifact"
        role="listitem"
        :aria-label="`Open artifact ${artifactName}`"
        tabindex="0"
        @click="handleArtifactChipActivate"
        @keydown="handleArtifactChipKeydown"
      >
        <FileText :size="11" aria-hidden="true" />
        {{ artifactName }}
      </button>

      <button
        v-if="activeSmartLinks.length > 0"
        type="button"
        class="refresh-timer-btn"
        role="listitem"
        :aria-label="isRefreshing ? 'Refreshing smart links…' : `Refresh smart links now (next refresh in ${secondsUntilRefresh}s)`"
        :title="isRefreshing ? 'Refreshing…' : `Refresh now (next refresh in ${secondsUntilRefresh}s)`"
        :disabled="isRefreshing"
        @click="handleSmartLinksRefreshNow"
      >
        <svg
          class="refresh-arc"
          :class="{ 'refresh-arc--spinning': isRefreshing }"
          width="16"
          height="16"
          viewBox="0 0 18 18"
          aria-hidden="true"
        >
          <circle
            class="arc-track"
            cx="9"
            cy="9"
            :r="ARC_RADIUS"
            fill="none"
            stroke-width="2"
          />
          <circle
            class="arc-fill"
            cx="9"
            cy="9"
            :r="ARC_RADIUS"
            fill="none"
            stroke-width="2"
            :stroke-dasharray="ARC_CIRCUMFERENCE"
            :stroke-dashoffset="arcOffset"
            stroke-linecap="round"
            transform="rotate(-90 9 9)"
          />
        </svg>
      </button>
    </div>

    <!-- Expandable todo strip -->
    <article
      v-if="todos.length > 0 && isTodosExpanded"
      class="session-section-card"
      role="region"
      aria-label="Session todo list"
    >
      <TodoListView
        :todos="todos"
        aria-label="Session todo list"
      />
    </article>

    <!-- Smart links (PR / issue) detail cards -->
    <article
      v-if="activeSmartLinks.length > 0"
      class="session-section-card"
      role="region"
      aria-label="Smart links"
    >
      <div
        v-if="smartLinkPRs.length > 0"
        class="smart-links-group"
      >
        <p class="smart-links-group__heading">
          Pull requests
        </p>
        <SmartLinkItem
          v-for="link in smartLinkPRs"
          :key="link.id"
          :link="link"
          :session-id="sessionId"
          @dismiss="handleDismissSmartLink"
        />
      </div>

      <div
        v-if="smartLinkIssues.length > 0"
        class="smart-links-group"
      >
        <p class="smart-links-group__heading">
          Issues
        </p>
        <SmartLinkItem
          v-for="link in smartLinkIssues"
          :key="link.id"
          :link="link"
          :session-id="sessionId"
          @dismiss="handleDismissSmartLink"
        />
      </div>
    </article>

    <ConfirmDeleteSessionDialog
      v-model:open="isDeleteDialogOpen"
      :is-deleting="isDeleting"
      :session-title="sessionTitle"
      @confirm="void handleDeleteConfirmed()"
    />

    <ForkSessionDialog
      v-if="canFork"
      :open="isForkDialogOpen"
      :session-id="sessionId ?? ''"
      :source-title="sessionTitle"
      @update:open="isForkDialogOpen = $event"
    />
  </header>
</template>

<style scoped>
.session-metadata-header {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.session-section-card {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 7px;
  border: 1px solid var(--border);
  border-radius: 10px;
  background: var(--card-bg);
}

.smart-links-group {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.smart-links-group__heading {
  margin: 0;
  font-size: 9.5px;
  font-weight: 700;
  letter-spacing: 0.03em;
  line-height: 1.2;
  text-transform: uppercase;
  color: var(--muted);
}

/* ---- Compact icon toolbar ---- */

.session-action-toolbar {
  display: flex;
  align-items: center;
  gap: 2px;
  flex-wrap: wrap;
  margin-bottom: 0;
}

.session-action-toolbar__btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  padding: 0;
  border: 1px solid var(--border);
  border-radius: 6px;
  background: transparent;
  color: var(--text);
  cursor: pointer;
}

.session-action-toolbar__btn :deep(svg) {
  width: 12px;
  height: 12px;
}

.session-action-toolbar__btn:hover:not(:disabled) {
  background: rgba(255, 255, 255, 0.1);
}

.session-action-toolbar__btn:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}

.session-action-toolbar__btn:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}

.session-action-toolbar__btn--danger {
  border-color: rgba(239, 68, 68, 0.35);
  color: #fca5a5;
}

.session-action-toolbar__divider {
  width: 1px;
  height: 16px;
  margin-inline: 2px;
  background: var(--border);
}

.session-action-toolbar__spinner {
  animation: session-metadata-header-spin 0.8s linear infinite;
}

.session-action-toolbar__error {
  width: 100%;
  margin: 2px 0 0;
  font-size: 10px;
  color: var(--error);
}

@keyframes session-metadata-header-spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}

/* ---- Chip row ---- */

.session-meta-chips {
  display: flex;
  align-items: center;
  gap: 5px;
  flex-wrap: wrap;
}

.meta-chip {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 3px 8px;
  border: 1px solid var(--border);
  border-radius: 10px;
  background: rgba(255, 255, 255, 0.03);
  color: var(--muted);
  font-size: 11px;
  cursor: pointer;
}

.meta-chip:hover {
  background: rgba(255, 255, 255, 0.08);
}

.meta-chip:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}

.meta-chip--todo {
  color: #fbbf24;
  border-color: rgba(245, 158, 11, 0.28);
  background: rgba(245, 158, 11, 0.08);
}

.meta-chip--artifact {
  color: #d8b4fe;
  border-color: rgba(192, 132, 252, 0.3);
  background: rgba(192, 132, 252, 0.08);
  font-weight: 500;
}

/* Smart links refresh arc timer */
.refresh-timer-btn {
  flex-shrink: 0;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  padding: 0;
  border: 0;
  border-radius: 4px;
  background: transparent;
  cursor: pointer;
  color: var(--muted);
}

.refresh-timer-btn:hover:not(:disabled),
.refresh-timer-btn:focus-visible:not(:disabled) {
  background: rgba(255, 255, 255, 0.06);
  color: var(--text);
  outline: none;
}

.refresh-timer-btn:disabled {
  cursor: default;
}

.refresh-arc {
  display: block;
}

.arc-track {
  stroke: rgba(255, 255, 255, 0.1);
}

.arc-fill {
  stroke: currentColor;
  transition: stroke-dashoffset 0.9s linear;
}

.refresh-arc--spinning .arc-fill {
  animation: arc-spin 1s linear infinite;
  stroke-dashoffset: 11;
}

@keyframes arc-spin {
  from { transform: rotate(-90deg); transform-origin: 9px 9px; }
  to { transform: rotate(270deg); transform-origin: 9px 9px; }
}
</style>
