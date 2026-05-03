<script setup lang="ts">
import { computed, ref, shallowRef, watch } from "vue";
import { useRouter } from "@tanstack/vue-router";
import ConfirmDeleteSessionDialog from "@/components/sessions/ConfirmDeleteSessionDialog.vue";
import FilesChanged from "@/components/session/FilesChanged.vue";
import ForkSessionDialog from "@/components/session/ForkSessionDialog.vue";
import SmartLinkItem from "@/plugins/builtin/smart-links/SmartLinkItem.vue";
import TodoListView from "@/components/session/TodoListView.vue";
import TokenGrid from "@/components/session/TokenGrid.vue";
import { useSessionTodos } from "@/composables/use-session-todos";
import {
  useAbortSession,
  useArchiveSession,
  useDeleteSession,
  useRenameSession,
  useResumeSession,
  useTerminateSession,
  useUnarchiveSession,
} from "@/composables/use-session-actions";
import { useDiffs } from "@/composables/use-diffs";
import { apiFetch } from "@/lib/api-client";
import type { SessionListItem } from "@/lib/api-types";
import { useSessionsStore } from "@/stores/sessions";
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
const sessionsStore = useSessionsStore();
const smartLinksStore = useSmartLinksStore();

const { abortSession, isAborting, error: abortError } = useAbortSession();
const { archiveSession, isArchiving, error: archiveError } = useArchiveSession();
const { deleteSession, isDeleting, error: deleteError } = useDeleteSession();
const { renameSession, isLoading: isRenaming, error: renameError } = useRenameSession();
const {
  resumeSession,
  isResuming,
  resumingSessionId,
  error: resumeError,
} = useResumeSession();
const { terminateSession, isTerminating, error: terminateError } = useTerminateSession();
const { unarchiveSession, isUnarchiving, error: unarchiveError } = useUnarchiveSession();

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
const isBusySession = computed(() => effectiveActivityStatus.value === "busy");
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
      const detailResponse = await apiFetch(`/api/sessions/${encodeURIComponent(nextSessionId)}`, {
        signal: controller.signal,
      });

      if (!detailResponse.ok) {
        throw new Error(`HTTP ${detailResponse.status}`);
      }

      const detail = (await detailResponse.json()) as SessionApiDetail;
      remoteSessionDetail.value = detail;
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
    const response = await resumeSession(sessionId.value);
    refreshPanelData();
    await router.navigate({
      to: "/sessions/$id",
      params: { id: response.session.id },
      search: {
        instanceId: response.instanceId,
        parentSessionId: undefined,
      },
    });
  } catch {
    // Error is exposed inline by the composable.
  }
}

async function handleStop(): Promise<void> {
  if (!sessionId.value || !resolvedInstanceId.value || !canStop.value) {
    return;
  }

  try {
    await terminateSession(sessionId.value, resolvedInstanceId.value);
    sessionsStore.patchSession(sessionId.value, {
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
    sessionsStore.patchSession(sessionId.value, {
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
    sessionsStore.patchSession(sessionId.value, {
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

function normalizeLifecycleStatus(value: string | null | undefined): "running" | "completed" | "stopped" | "error" | "disconnected" | null {
  switch (value) {
    case "active":
    case "idle":
    case "waiting_input":
    case "running":
      return "running";
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

function normalizeActivityStatus(value: string | null | undefined): "busy" | "idle" | "waiting_input" | null {
  switch (value) {
    case "active":
    case "busy":
      return "busy";
    case "idle":
      return "idle";
    case "waiting_input":
      return "waiting_input";
    default:
      return null;
  }
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
    const response = await apiFetch(`/api/sessions/${sid}/smart-links/${linkId}/dismiss`, {
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
      <div class="session-section-card__header">
        <p class="session-section-card__title">
          Session actions
        </p>
      </div>

      <div class="session-action-grid">
        <button
          v-if="canAbort"
          type="button"
          data-testid="abort-button"
          class="session-action-button session-action-button--danger"
          :disabled="isAnyActionPending || !sessionId || !resolvedInstanceId"
          :aria-busy="isAborting ? 'true' : 'false'"
          @click="handleAbort"
        >
          <span
            v-if="isAborting"
            class="session-action-button__spinner"
            aria-hidden="true"
          />
          <span>{{ isAborting ? "Aborting..." : "Abort" }}</span>
        </button>

        <button
          v-if="canResume"
          type="button"
          data-testid="session-resume-button"
          class="session-action-button"
          :disabled="isAnyActionPending || !sessionId"
          :aria-busy="isResumingCurrentSession ? 'true' : 'false'"
          @click="handleResume"
        >
          <span
            v-if="isResumingCurrentSession"
            class="session-action-button__spinner"
            aria-hidden="true"
          />
          <span>{{ isResumingCurrentSession ? "Resuming..." : "Resume" }}</span>
        </button>

        <button
          v-if="canStop"
          type="button"
          data-testid="session-stop-button"
          class="session-action-button session-action-button--danger"
          :disabled="isAnyActionPending || !sessionId || !resolvedInstanceId"
          :aria-busy="isTerminating ? 'true' : 'false'"
          @click="handleStop"
        >
          <span
            v-if="isTerminating"
            class="session-action-button__spinner"
            aria-hidden="true"
          />
          <span>{{ isTerminating ? "Stopping..." : "Stop" }}</span>
        </button>

        <button
          type="button"
          data-testid="session-archived-fork-button"
          class="session-action-button"
          :disabled="isAnyActionPending || !sessionId"
          :aria-busy="false"
          @click="handleFork"
        >
          <span>Fork</span>
        </button>

        <button
          type="button"
          class="session-action-button"
          :disabled="isAnyActionPending || !sessionId"
          :aria-busy="isRenaming ? 'true' : 'false'"
          @click="handleRename"
        >
          <span
            v-if="isRenaming"
            class="session-action-button__spinner"
            aria-hidden="true"
          />
          <span>{{ isRenaming ? "Renaming..." : "Rename" }}</span>
        </button>

        <button
          type="button"
          data-testid="session-delete-button"
          class="session-action-button session-action-button--danger"
          :disabled="isAnyActionPending || !sessionId || !resolvedInstanceId"
          :aria-busy="isDeleting ? 'true' : 'false'"
          @click="handleDelete"
        >
          <span
            v-if="isDeleting"
            class="session-action-button__spinner"
            aria-hidden="true"
          />
          <span>{{ isDeleting ? "Deleting..." : "Delete" }}</span>
        </button>

        <button
          v-if="canArchive"
          type="button"
          data-testid="session-archive-banner-button"
          class="session-action-button"
          :disabled="isAnyActionPending || !sessionId"
          :aria-busy="isArchiving ? 'true' : 'false'"
          @click="handleArchive"
        >
          <span
            v-if="isArchiving"
            class="session-action-button__spinner"
            aria-hidden="true"
          />
          <span>{{ isArchiving ? "Archiving..." : "Archive" }}</span>
        </button>

        <button
          v-if="canUnarchive"
          type="button"
          data-testid="session-unarchive-button"
          class="session-action-button"
          :disabled="isAnyActionPending || !sessionId"
          :aria-busy="isUnarchiving ? 'true' : 'false'"
          @click="handleUnarchive"
        >
          <span
            v-if="isUnarchiving"
            class="session-action-button__spinner"
            aria-hidden="true"
          />
          <span>{{ isUnarchiving ? "Updating..." : "Unarchive" }}</span>
        </button>
      </div>

      <p
        v-for="message in actionErrors"
        :key="message"
        class="session-section-card__note session-section-card__note--error"
        role="alert"
      >
        {{ message }}
      </p>
    </article>

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

.session-action-grid {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.session-action-button {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  min-height: 32px;
  padding: 0 12px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: rgba(255, 255, 255, 0.03);
  color: var(--text);
  font-size: 11px;
  font-weight: 600;
  cursor: pointer;
  transition: background-color 0.15s ease, border-color 0.15s ease, opacity 0.15s ease;
}

.session-action-button:hover:not(:disabled) {
  background: rgba(255, 255, 255, 0.08);
}

.session-action-button:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.session-action-button:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.session-action-button--danger {
  border-color: rgba(239, 68, 68, 0.35);
  color: #fca5a5;
}

.session-action-button__spinner {
  width: 12px;
  height: 12px;
  border: 2px solid currentColor;
  border-right-color: transparent;
  border-radius: 999px;
  animation: session-detail-panel-spin 0.8s linear infinite;
}

@keyframes session-detail-panel-spin {
  to {
    transform: rotate(360deg);
  }
}

.session-section-card__count {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 20px;
  height: 18px;
  padding: 0 6px;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.08);
  font-size: 10px;
  font-weight: 700;
  color: var(--muted);
}

.smart-links-group {
  display: flex;
  flex-direction: column;
}

.smart-links-group__heading {
  margin: 0;
  padding: 4px 8px;
  font-size: 10px;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--muted);
}

</style>
