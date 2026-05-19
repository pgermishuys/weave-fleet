<script setup lang="ts">
import { computed, ref, shallowRef, watch } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { Archive, ArchiveRestore, GitFork, Loader2, OctagonX, Pencil, RotateCcw, Square, Trash2 } from "lucide-vue-next";
import ConfirmDeleteSessionDialog from "@/components/sessions/ConfirmDeleteSessionDialog.vue";
import FilesChanged from "@/components/session/FilesChanged.vue";
import ForkSessionDialog from "@/components/session/ForkSessionDialog.vue";
import SmartLinkItem from "@/plugins/builtin/smart-links/SmartLinkItem.vue";
import TodoListView from "@/components/session/TodoListView.vue";
import TokenGrid from "@/components/session/TokenGrid.vue";
import { useSessionTodos } from "@/composables/use-session-todos";
import { useSessionDetailContext } from "@/composables/use-session-detail-context";
import { useDiffs } from "@/composables/use-diffs";
import { apiFetch } from "@/lib/api-client";
import { trackAction } from "@/lib/track-action";
import type { SessionListItem } from "@/lib/api-types";
import { useSmartLinksStore } from "@/stores/smart-links";

interface TokenMetric {
  id: string;
  label: string;
  value: string;
  helper: string;
}

interface ChangedFile {
  path: string;
  additions: number;
  deletions: number;
}

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
  totalTokens?: number;
  totalCost?: number;
  harnessType?: string;
  workspaceId?: string;
  projectId?: string | null;
}

const props = defineProps<{
  session: SessionListItem | null;
}>();

const router = useRouter();
const ctx = useSessionDetailContext();
const smartLinksStore = useSmartLinksStore();

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
const { unarchiveSession, isUnarchiving, error: unarchiveError } = ctx.unarchive;

const remoteSessionDetail = ref<SessionApiDetail | null>(null);
const filesChanged = ref<ChangedFile[]>([]);
const refreshVersion = shallowRef(0);
const isDeleteDialogOpen = shallowRef(false);
const isForkDialogOpen = shallowRef(false);

const isLoadingFilesChanged = shallowRef(false);
const filesChangedError = shallowRef<string | null>(null);

const sessionId = computed(() => props.session?.session.id ?? null);
const activeSmartLinks = computed(() => sessionId.value ? smartLinksStore.getActiveLinks(sessionId.value) : []);
const smartLinkPRs = computed(() => activeSmartLinks.value.filter(l => l.resourceType === "pull_request"));
const smartLinkIssues = computed(() => activeSmartLinks.value.filter(l => l.resourceType !== "pull_request"));
const resolvedInstanceId = computed(() => normalizeString(props.session?.instanceId) ?? normalizeString(remoteSessionDetail.value?.instanceId));
const todoSessionId = computed(() => sessionId.value ?? "");
const todoInstanceId = computed(() => resolvedInstanceId.value ?? "");
const totalTokens = computed(() => props.session?.totalTokens ?? remoteSessionDetail.value?.totalTokens ?? null);
const totalCostUsd = computed(() => props.session?.totalCost ?? remoteSessionDetail.value?.totalCost ?? null);
const effectiveIsolationStrategy = computed(() => props.session?.isolationStrategy ?? remoteSessionDetail.value?.isolationStrategy);
const isolationLabel = computed(() => formatIsolationStrategy(effectiveIsolationStrategy.value));
const sessionTitle = computed(() => normalizeString(props.session?.session.title) ?? normalizeString(remoteSessionDetail.value?.title) ?? "Untitled session");
const effectiveSessionStatus = computed(() => props.session?.sessionStatus
  ?? remoteSessionDetail.value?.lifecycleStatus
  ?? remoteSessionDetail.value?.status
  ?? null);
const { todos } = useSessionTodos(todoSessionId, todoInstanceId);
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
const canAbort = computed(() => isRunningSession.value && isBusySession.value);
const canResume = computed(() => {
  switch (effectiveLifecycleStatus.value) {
    case "stopped":
    case "completed":
    case "disconnected":
      return true;
    default:
      return false;
  }
});
const canStop = computed(() => isRunningSession.value);
const canArchive = computed(() => effectiveRetentionStatus.value !== "archived" && !isRunningSession.value);
const canUnarchive = computed(() => effectiveRetentionStatus.value === "archived");
const isResumingCurrentSession = computed(() => isResuming.value && resumingSessionId.value === sessionId.value);
const isAnyActionPending = computed(() => isAborting.value
  || isArchiving.value
  || isDeleting.value
  || isRenaming.value
  || isResumingCurrentSession.value
  || isTerminating.value
  || isUnarchiving.value);
const actionErrors = computed(() => [
  abortError.value,
  archiveError.value,
  deleteError.value,
  renameError.value,
  resumeError.value,
  terminateError.value,
  unarchiveError.value,
].filter((message): message is string => Boolean(message)));

const tokenMetrics = computed<readonly TokenMetric[]>(() => [
  {
    id: "tokens",
    label: "Total tokens",
    value: formatNumber(totalTokens.value),
    helper: "Across all session messages",
  },
  {
    id: "cost",
    label: "Total cost",
    value: formatCurrency(totalCostUsd.value),
    helper: "Estimated session spend",
  },
  {
    id: "files",
    label: "Files changed",
    value: isLoadingFilesChanged.value ? "…" : filesChanged.value.length.toLocaleString(),
    helper: filesChangedError.value ? "Diff summary unavailable" : "Tracked via session diffs",
  },
  {
    id: "isolation",
    label: "Isolation",
    value: isolationLabel.value,
    helper: "Workspace strategy",
  },
]);

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

watch(
  [sessionId, resolvedInstanceId, refreshVersion],
  async ([nextSessionId, instanceId]) => {
    filesChanged.value = [];
    filesChangedError.value = null;

    if (!nextSessionId || !instanceId) {
      return;
    }

    isLoadingFilesChanged.value = true;

    try {
      const diffState = useDiffs(nextSessionId, instanceId);
      await diffState.fetchDiffs();

      filesChanged.value = diffState.diffs.value.map((diff) => ({
        path: diff.file,
        additions: diff.additions,
        deletions: diff.deletions,
      }));
      filesChangedError.value = diffState.error.value ?? null;
    } finally {
      isLoadingFilesChanged.value = false;
    }
  },
  { immediate: true },
);

async function handleAbort(): Promise<void> {
  if (!sessionId.value || !resolvedInstanceId.value || !canAbort.value) {
    return;
  }

  try {
    await abortSession(sessionId.value, resolvedInstanceId.value);
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
  if (!sessionId.value) {
    return;
  }

  isForkDialogOpen.value = true;
}

async function handleDelete(): Promise<void> {
  if (!sessionId.value || !resolvedInstanceId.value) {
    return;
  }

  isDeleteDialogOpen.value = true;
}

async function handleDeleteConfirmed(): Promise<void> {
  if (!sessionId.value || !resolvedInstanceId.value) {
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

async function handleUnarchive(): Promise<void> {
  if (!sessionId.value || !canUnarchive.value) {
    return;
  }

  try {
    await unarchiveSession(sessionId.value);
    ctx.patchSession(sessionId.value, {
      retentionStatus: "active",
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

function formatIsolationStrategy(strategy: string | null | undefined): string {
  switch (strategy) {
    case "existing":
      return "Existing";
    case "worktree":
      return "Worktree";
    case "clone":
      return "Clone";
    default:
      return "Unknown";
  }
}

function formatNumber(value: number | null): string {
  if (value === null) {
    return "—";
  }

  return value.toLocaleString();
}

function formatCurrency(amount: number | null): string {
  if (amount === null) {
    return "—";
  }

  return new Intl.NumberFormat("en-US", {
    style: "currency",
    currency: "USD",
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  }).format(amount);
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
  <section
    class="session-detail-panel"
    aria-label="Session details"
  >
    <!-- Compact icon toolbar (V1 layout) -->
    <div
      v-if="ctx.actionsLayout === 'toolbar'"
      class="session-action-toolbar"
    >
      <button
        v-if="canAbort"
        type="button"
        data-testid="abort-button"
        class="session-action-toolbar__btn session-action-toolbar__btn--danger"
        :disabled="isAnyActionPending || !sessionId || !resolvedInstanceId"
        title="Abort"
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
        v-if="ctx.supportsFork"
        type="button"
        data-testid="session-archived-fork-button"
        class="session-action-toolbar__btn"
        :disabled="isAnyActionPending || !sessionId"
        title="Fork"
        @click="handleFork"
      >
        <GitFork
          :size="14"
          aria-hidden="true"
        />
      </button>

      <button
        type="button"
        class="session-action-toolbar__btn"
        :disabled="isAnyActionPending || !sessionId"
        title="Rename"
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
        type="button"
        data-testid="session-delete-button"
        class="session-action-toolbar__btn session-action-toolbar__btn--danger"
        :disabled="isAnyActionPending || !sessionId || !resolvedInstanceId"
        title="Delete"
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

      <button
        v-if="canUnarchive && ctx.supportsArchive"
        type="button"
        data-testid="session-unarchive-button"
        class="session-action-toolbar__btn"
        :disabled="isAnyActionPending || !sessionId"
        title="Unarchive"
        @click="handleUnarchive"
      >
        <Loader2
          v-if="isUnarchiving"
          :size="14"
          class="session-action-toolbar__spinner"
          aria-hidden="true"
        />
        <ArchiveRestore
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

    <article class="session-section-card">
      <div class="session-section-card__header">
        <p class="session-section-card__title">
          Todo list
        </p>
      </div>

      <TodoListView
        :todos="todos"
        aria-label="Session todo list"
        :empty-message="sessionId ? 'No todos yet.' : 'Select a session to view todos.'"
      />
    </article>

    <TokenGrid :metrics="tokenMetrics" />

    <article class="session-section-card">
      <p
        v-if="filesChangedError && !isLoadingFilesChanged"
        class="session-section-card__note"
      >
        File diff data is unavailable right now.
      </p>

      <FilesChanged :files="filesChanged" />
    </article>

    <article
      v-if="activeSmartLinks.length > 0"
      class="session-section-card"
    >
      <div class="session-section-card__header">
        <p class="session-section-card__title">
          Smart links
        </p>
        <span class="session-section-card__count">{{ activeSmartLinks.length }}</span>
      </div>

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
      v-if="ctx.supportsFork"
      :open="isForkDialogOpen"
      :session-id="sessionId ?? ''"
      :source-title="sessionTitle"
      @update:open="isForkDialogOpen = $event"
    />
  </section>
</template>

<style scoped>
.session-detail-panel {
  display: flex;
  flex-direction: column;
}

.session-section-card {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 12px;
  margin-bottom: 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
}

.session-section-card__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.session-section-card__title {
  margin: 0;
  font-size: 11px;
  font-weight: 600;
  color: var(--text);
}

.session-section-card__note {
  margin: 0;
  font-size: 11px;
  color: var(--muted);
}

.session-section-card__note--error {
  color: var(--error);
}

/* ---- Compact icon toolbar (V1 layout) ---- */

.session-action-toolbar {
  display: flex;
  align-items: center;
  gap: 4px;
  flex-wrap: wrap;
}

.session-action-toolbar__btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  padding: 0;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--text);
  cursor: pointer;
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
  height: 18px;
  background: var(--border);
}

.session-action-toolbar__spinner {
  animation: session-detail-panel-spin 0.8s linear infinite;
}

.session-action-toolbar__error {
  width: 100%;
  margin: 4px 0 0;
  font-size: 11px;
  color: var(--error);
}

@keyframes session-detail-panel-spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}

</style>
