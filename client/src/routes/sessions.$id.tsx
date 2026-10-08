import { createFileRoute } from "@tanstack/vue-router";
import { computed, defineComponent, nextTick, shallowRef, watch, type ComponentPublicInstance } from "vue";
import { ArrowLeft, Bot } from "lucide-vue-next";
import { storeToRefs } from "pinia";
import ConfirmDeleteSessionDialog from "@/components/sessions/ConfirmDeleteSessionDialog.vue";
import ActivityStream from "@/components/session/ActivityStream.vue";
import Composer from "@/components/session/Composer.vue";
import RecapLine from "@/components/session/RecapLine.vue";
import SideConversationPanel from "@/components/session/SideConversationPanel.vue";
import SubagentPromptLine from "@/components/session/SubagentPromptLine.vue";
import DiffsTray from "@/components/session/DiffsTray.vue";
import FilesChangedView from "@/components/session/FilesChangedView.vue";
import ForkSessionDialog from "@/components/session/ForkSessionDialog.vue";
import SessionActionToolbar from "@/components/session/SessionActionToolbar.vue";
import SessionDetailHeader from "@/components/session/SessionDetailHeader.vue";
import TerminalDrawer from "@/components/terminal/TerminalDrawer.vue";
import WorkflowFinishBar from "@/components/workflows/WorkflowFinishBar.vue";
import WorkflowRunCard from "@/components/workflows/WorkflowRunCard.vue";
import WorkflowStepper from "@/components/workflows/WorkflowStepper.vue";
import TerminalToggleButton from "@/components/terminal/TerminalToggleButton.vue";
import RightPanelSheetButton from "@/components/layout/RightPanelSheetButton.vue";
import { useDiffs } from "@/composables/use-diffs";
import {
  useAbortSession,
  useDeleteSession,
  useRenameSession,
} from "@/composables/use-session-actions";
import { useSentPrompts } from "@/composables/use-send-prompt";
import { provideSessionDiffsContext } from "@/composables/use-session-diffs-context";
import { useSessionRecap } from "@/composables/use-session-recap";
import { useSessionTerminals } from "@/composables/use-session-terminals";
import { lineageOf } from "@/lib/session-lineage";
import { apiFetchOn } from "@/lib/api-client";
import { useMachineTarget } from "@/lib/machine-target";
import type { SessionActionCapabilities, SessionListItem, SessionOrigin } from "@/api/client";
import type { SessionActivityStatus } from "@/lib/types";
import { dispatchSessionUpsert } from "@/lib/session-sync";
import { useSessionsStore } from "@/stores/sessions";
import { useArchiveQueueStore } from "@/stores/archive-queue";

function normalizeRetentionStatus(value: string | null | undefined): "active" | "archived" {
  return value === "archived" ? "archived" : "active";
}

interface SessionDetailResponse {
  id?: string | null;
  instanceId?: string | null;
  parentSessionId?: string | null;
  workspaceId?: string | null;
  workspaceDirectory?: string | null;
  workspaceDisplayName?: string | null;
  sourceDirectory?: string | null;
  isolationStrategy?: string | null;
  branch?: string | null;
  title?: string | null;
  createdAt?: string | null;
  projectId?: string | null;
  projectName?: string | null;
  status?: string | null;
  activityStatus?: string | null;
  lifecycleStatus?: string | null;
  retentionStatus?: string | null;
  totalTokens?: number | null;
  totalCost?: number | null;
  capabilities?: SessionActionCapabilities;
  origin?: SessionOrigin | null;
  harnessType?: string | null;
  /** The profile the session started with, if it has one. */
  harnessProfileName?: string | null;
  tags?: string[];
  forkedFromSessionId?: string | null;
  spawnedBySessionId?: string | null;
  spawnKind?: string | null;
  lineageDetachedAt?: string | null;
  /** While the harness waits to retry a failed model call: which attempt, out of how many, why and when. */
  retryAttempt?: number | null;
  retryMaxAttempts?: number | null;
  retryMessage?: string | null;
  retryNext?: string | null;
}

type ComposerInstance = ComponentPublicInstance & {
  focusPrompt: () => void;
};

type SessionViewMode = "chat" | "files-changed";

function getStringField(
  value: Record<string, unknown>,
  camelKey: string,
  pascalKey: string,
): string | null | undefined {
  const candidate = value[camelKey] ?? value[pascalKey];
  return typeof candidate === "string" ? candidate : candidate == null ? null : undefined;
}

function getNumberField(
  value: Record<string, unknown>,
  camelKey: string,
  pascalKey: string,
): number | null | undefined {
  const candidate = value[camelKey] ?? value[pascalKey];
  return typeof candidate === "number" ? candidate : candidate == null ? null : undefined;
}

function normalizeSessionDetailResponse(payload: unknown): SessionDetailResponse {
  if (!payload || typeof payload !== "object") {
    return {};
  }

  const value = payload as Record<string, unknown>;

  const originPayload = value.origin ?? value.Origin;
  const origin = originPayload && typeof originPayload === "object"
    ? {
      sourceType: getStringField(originPayload as Record<string, unknown>, "sourceType", "SourceType") ?? "",
      title: getStringField(originPayload as Record<string, unknown>, "title", "Title") ?? null,
      resourceUrl: getStringField(originPayload as Record<string, unknown>, "resourceUrl", "ResourceUrl") ?? null,
      resourceId: getStringField(originPayload as Record<string, unknown>, "resourceId", "ResourceId") ?? null,
      providerId: getStringField(originPayload as Record<string, unknown>, "providerId", "ProviderId") ?? "",
    } satisfies SessionOrigin
    : originPayload == null
      ? null
      : undefined;

  return {
    id: getStringField(value, "id", "Id"),
    instanceId: getStringField(value, "instanceId", "InstanceId"),
    parentSessionId: getStringField(value, "parentSessionId", "ParentSessionId"),
    forkedFromSessionId: getStringField(value, "forkedFromSessionId", "ForkedFromSessionId"),
    spawnedBySessionId: getStringField(value, "spawnedBySessionId", "SpawnedBySessionId"),
    spawnKind: getStringField(value, "spawnKind", "SpawnKind"),
    workspaceId: getStringField(value, "workspaceId", "WorkspaceId"),
    workspaceDirectory: getStringField(value, "workspaceDirectory", "WorkspaceDirectory"),
    workspaceDisplayName: getStringField(value, "workspaceDisplayName", "WorkspaceDisplayName"),
    sourceDirectory: getStringField(value, "sourceDirectory", "SourceDirectory"),
    isolationStrategy: getStringField(value, "isolationStrategy", "IsolationStrategy"),
    branch: getStringField(value, "branch", "Branch"),
    title: getStringField(value, "title", "Title"),
    createdAt: getStringField(value, "createdAt", "CreatedAt"),
    projectId: getStringField(value, "projectId", "ProjectId"),
    projectName: getStringField(value, "projectName", "ProjectName"),
    status: getStringField(value, "status", "Status"),
    activityStatus: getStringField(value, "activityStatus", "ActivityStatus"),
    lifecycleStatus: getStringField(value, "lifecycleStatus", "LifecycleStatus"),
    retentionStatus: getStringField(value, "retentionStatus", "RetentionStatus"),
    totalTokens: getNumberField(value, "totalTokens", "TotalTokens"),
    totalCost: getNumberField(value, "totalCost", "TotalCost"),
    capabilities: (value.capabilities ?? value.Capabilities) as SessionActionCapabilities | undefined,
    origin,
    harnessType: getStringField(value, "harnessType", "HarnessType"),
    harnessProfileName: getStringField(value, "harnessProfileName", "HarnessProfileName"),
    tags: Array.isArray(value.tags ?? value.Tags) ? (value.tags ?? value.Tags) as string[] : undefined,
    retryAttempt: getNumberField(value, "retryAttempt", "RetryAttempt"),
    retryMaxAttempts: getNumberField(value, "retryMaxAttempts", "RetryMaxAttempts"),
    retryMessage: getStringField(value, "retryMessage", "RetryMessage"),
    retryNext: getStringField(value, "retryNext", "RetryNext"),
  };
}

function sessionTimeFromCreatedAt(createdAt: string | null | undefined): { created: number; updated: number } {
  const createdMs = createdAt ? Date.parse(createdAt) : Number.NaN;
  const created = Number.isFinite(createdMs) ? createdMs : Date.now();
  return { created, updated: created };
}

function normalizeLifecycleStatus(value: string | null | undefined): "running" | "completed" | "stopped" | "error" | "disconnected" | null {
  switch (value) {
    case "active":
    case "delegating":
    case "idle":
    case "waiting_input":
    case "running":
      return "running";
    case "complete":
    case "completed":
      return "completed";
    case "error":
      return "error";
    case "disconnected":
      return "disconnected";
    case "stopped":
      return "stopped";
    default:
      return null;
  }
}

function normalizeActivityStatus(value: string | null | undefined): SessionActivityStatus | null {
  switch (value) {
    case "active":
    case "busy":
      return "busy";
    case "delegating":
      return "delegating";
    case "retry":
      return "retry";
    case "waiting_input":
      return "waiting_input";
    case "idle":
      return "idle";
    default:
      return null;
  }
}

// A retrying session is waiting out a model error mid-turn: still working, never idle.
function isActiveActivityStatus(value: string | null | undefined): value is "busy" | "delegating" | "retry" {
  return value === "busy" || value === "delegating" || value === "retry";
}

function isDiffStalingStatus(
  activityStatus: SessionActivityStatus | null | undefined,
  lifecycleStatus: string | null | undefined,
): boolean {
  return isActiveActivityStatus(activityStatus) || lifecycleStatus === "running" && activityStatus === "waiting_input";
}

const SessionDetailPage = defineComponent({
  name: "SessionDetailPage",
  setup(_props, { expose }) {
    const machine = useMachineTarget();
    const params = Route.useParams();
    useSessionTerminals(() => params.value.id);
    const recap = useSessionRecap(() => params.value.id);
    const search = Route.useSearch();
    const navigate = Route.useNavigate();
    const sessionsStore = useSessionsStore();
    const { sessionStateOverrides } = storeToRefs(sessionsStore);
    const remoteSession = shallowRef<SessionDetailResponse | null>(null);
    /** The server has no session under this id (a stale link, or one deleted elsewhere). */
    const sessionMissing = shallowRef(false);
    const composerRef = shallowRef<ComposerInstance | null>(null);
    const viewMode = shallowRef<SessionViewMode>(search.value.view === "files" ? "files-changed" : "chat");
    const selectedChangedFile = shallowRef<{ file: string } | null>(null);
    const optimisticWorking = shallowRef(false);
    const isDeleteDialogOpen = shallowRef(false);
    const isForkDialogOpen = shallowRef(false);
    const isDiffsTrayOpen = shallowRef(false);
    const optimisticSessionState = shallowRef<{
      activityStatus?: string | null;
      lifecycleStatus?: string | null;
      retentionStatus?: string | null;
      sessionStatus?: string | null;
    } | null>(null);

    const { abortSession, isAborting, error: abortError } = useAbortSession();
    const archiveQueue = useArchiveQueueStore();
    const isEditingTitle = shallowRef(false);
    const isRestoring = shallowRef(false);
    const { deleteSession, isDeleting, error: deleteError } = useDeleteSession();
    const { renameSession, isLoading: isRenaming, error: renameError } = useRenameSession();

    const selectedSession = computed(() => {
      return sessionsStore.sessionById(params.value.id);
    });

    const sessionStateOverride = computed(() => {
      return sessionStateOverrides.value[params.value.id] ?? null;
    });

    watch(
      () => params.value.id,
      async (sessionId, _previousSessionId, onCleanup) => {
        sessionsStore.setActiveSessionId(sessionId ?? null);
        remoteSession.value = null;
        sessionMissing.value = false;
        isEditingTitle.value = false;

        if (!sessionId) {
          return;
        }

        const abortController = new AbortController();
        onCleanup(() => {
          abortController.abort();
        });

        void nextTick(() => {
          if (!abortController.signal.aborted) {
            composerRef.value?.focusPrompt();
          }
        });

        try {
          const response = await apiFetchOn(machine.connection, `/api/sessions/${encodeURIComponent(sessionId)}`, {
            signal: abortController.signal,
          });
          if (abortController.signal.aborted) {
            return;
          }
          if (!response.ok) {
            sessionMissing.value = response.status === 404;
            return;
          }

          const nextRemoteSession = normalizeSessionDetailResponse(await response.json());
          remoteSession.value = nextRemoteSession;

          const normalizedLifecycleStatus = normalizeLifecycleStatus(
            nextRemoteSession.lifecycleStatus ?? nextRemoteSession.status,
          ) ?? "running";
          const normalizedActivityStatus = normalizeActivityStatus(nextRemoteSession.activityStatus) ?? "idle";

          const nextSession = {
            instanceId: nextRemoteSession.instanceId ?? search.value.instanceId ?? selectedSession.value?.instanceId ?? "",
            workspaceId: nextRemoteSession.workspaceId ?? selectedSession.value?.workspaceId ?? "",
            workspaceDirectory: nextRemoteSession.workspaceDirectory ?? selectedSession.value?.workspaceDirectory ?? "",
            workspaceDisplayName: nextRemoteSession.workspaceDisplayName ?? selectedSession.value?.workspaceDisplayName ?? null,
            isolationStrategy: nextRemoteSession.isolationStrategy ?? selectedSession.value?.isolationStrategy ?? "existing",
            sessionStatus: normalizedLifecycleStatus === "running"
              ? normalizedActivityStatus === "waiting_input"
                ? "waiting_input"
                : isActiveActivityStatus(normalizedActivityStatus)
                ? "active"
                : "idle"
              : normalizedLifecycleStatus,
            session: {
              id: nextRemoteSession.id ?? sessionId,
              title: nextRemoteSession.title ?? selectedSession.value?.session.title ?? "Untitled session",
              // A session opened before the list has it (e.g. just created) needs its real age,
              // not the epoch, or the sidebar shows it as decades old.
              time: selectedSession.value?.session.time ?? sessionTimeFromCreatedAt(nextRemoteSession.createdAt),
              tags: nextRemoteSession.tags ?? selectedSession.value?.session.tags ?? [],
            },
            instanceStatus: selectedSession.value?.instanceStatus ?? "running",
            parentSessionId: nextRemoteSession.parentSessionId ?? selectedSession.value?.parentSessionId ?? null,
            forkedFromSessionId: nextRemoteSession.forkedFromSessionId ?? selectedSession.value?.forkedFromSessionId ?? null,
            spawnedBySessionId: nextRemoteSession.spawnedBySessionId ?? selectedSession.value?.spawnedBySessionId ?? null,
            spawnKind: nextRemoteSession.spawnKind ?? selectedSession.value?.spawnKind ?? null,
            sourceDirectory: nextRemoteSession.sourceDirectory ?? selectedSession.value?.sourceDirectory ?? null,
            branch: nextRemoteSession.branch ?? selectedSession.value?.branch ?? null,
            activityStatus: normalizedActivityStatus,
            lifecycleStatus: normalizedLifecycleStatus,
            retentionStatus: normalizeRetentionStatus(nextRemoteSession.retentionStatus),
            archivedAt: selectedSession.value?.archivedAt ?? null,
            typedInstanceStatus: selectedSession.value?.typedInstanceStatus ?? "running",
            isHidden: selectedSession.value?.isHidden ?? false,
            totalTokens: nextRemoteSession.totalTokens ?? selectedSession.value?.totalTokens,
            totalCost: nextRemoteSession.totalCost ?? selectedSession.value?.totalCost,
            // The server's project wins: a new session lands in Scratch, not "Ungrouped".
            projectId: nextRemoteSession.projectId ?? selectedSession.value?.projectId ?? null,
            projectName: nextRemoteSession.projectName ?? selectedSession.value?.projectName ?? null,
            capabilities: nextRemoteSession.capabilities ?? selectedSession.value?.capabilities,
            origin: nextRemoteSession.origin ?? selectedSession.value?.origin ?? null,
            harnessType: nextRemoteSession.harnessType ?? selectedSession.value?.harnessType ?? null,
            tags: nextRemoteSession.tags ?? selectedSession.value?.tags ?? [],
            // Which attempt a retrying session is on, why and when, so a page opened mid-retry says so.
            ...(normalizedActivityStatus === "retry"
              ? {
                  retryAttempt: nextRemoteSession.retryAttempt ?? selectedSession.value?.retryAttempt ?? null,
                  retryMaxAttempts: nextRemoteSession.retryMaxAttempts ?? selectedSession.value?.retryMaxAttempts ?? null,
                  retryMessage: nextRemoteSession.retryMessage ?? selectedSession.value?.retryMessage ?? null,
                  retryNext: nextRemoteSession.retryNext ?? selectedSession.value?.retryNext ?? null,
                }
              : {}),
          } satisfies SessionListItem;

          // The live machine's list holds only its own sessions.
          if (machine.isLive) sessionsStore.upsertSession(nextSession);
          else sessionsStore.upsertElsewhere(machine.key, nextSession);
          dispatchSessionUpsert(nextSession);
        } catch (error) {
          if (error instanceof DOMException && error.name === "AbortError") {
            return;
          }
        }
      },
      { immediate: true },
    );

    watch(
      () => search.value.view,
      (nextView) => {
        viewMode.value = nextView === "files" ? "files-changed" : "chat";
      },
      { immediate: true },
    );

    async function setViewMode(nextViewMode: SessionViewMode): Promise<void> {
      viewMode.value = nextViewMode;

      await navigate({
        to: "/sessions/$id",
        params: { id: params.value.id },
        search: {
          instanceId: search.value.instanceId,
          parentSessionId: search.value.parentSessionId,
          view: nextViewMode === "files-changed" ? "files" : undefined,
        },
        replace: true,
      });
    }

    expose({ setViewMode });

    const instanceId = computed<string | undefined>(() => {
      return search.value.instanceId
        ?? selectedSession.value?.instanceId
        ?? remoteSession.value?.instanceId
        ?? undefined;
    });

    const diffState = useDiffs(
      () => params.value.id,
    );
    function openDiffsTray(): void {
      if (viewMode.value !== "chat") {
        void setViewMode("chat");
      }

      isDiffsTrayOpen.value = true;

      if (!diffState.isLoading.value && (diffState.isStale.value || diffState.diffs.value.length === 0) && params.value.id) {
        void diffState.fetchDiffs();
      }
    }

    provideSessionDiffsContext(diffState, {
      isDiffsTrayOpen,
      openDiffsTray,
    });

    const fileDiffs = computed(() => diffState.diffs.value);
    const selectedFilesChangedViewFile = computed(() => {
      const selectedFilePath = selectedChangedFile.value?.file;
      if (selectedFilePath) {
        const currentSelection = fileDiffs.value.find((diff) => diff.file === selectedFilePath);
        if (currentSelection) {
          return currentSelection;
        }
      }

      return fileDiffs.value[0] ?? null;
    });

    watch(
      () => params.value.id,
      async (sessionId) => {
        if (sessionId) {
          await diffState.fetchDiffs();
        }
      },
      { immediate: true },
    );

    watch(
      () => params.value.id,
      () => {
        selectedChangedFile.value = null;
      },
    );

    const parentSession = computed(() => {
      const parentSessionId = search.value.parentSessionId ?? selectedSession.value?.parentSessionId ?? remoteSession.value?.parentSessionId ?? null;
      if (!parentSessionId) {
        return null;
      }

      return sessionsStore.sessionById(parentSessionId);
    });

    const parentSessionHref = computed(() => {
      const parentSessionId = search.value.parentSessionId ?? selectedSession.value?.parentSessionId ?? remoteSession.value?.parentSessionId ?? null;
      if (!parentSessionId) {
        return null;
      }

      if (!parentSession.value?.instanceId) {
        return `/sessions/${parentSessionId}`;
      }

      return `/sessions/${parentSessionId}?instanceId=${parentSession.value.instanceId}`;
    });

    const isDelegatedSession = computed(() => {
      return Boolean(search.value.parentSessionId || selectedSession.value?.parentSessionId || remoteSession.value?.parentSessionId);
    });

    // "Started by …" in the header: a fork or a session another session's agent started (a subagent has its banner).
    const startedBy = computed(() => {
      const link = lineageOf({
        parentSessionId: null,
        forkedFromSessionId: selectedSession.value?.forkedFromSessionId ?? remoteSession.value?.forkedFromSessionId ?? null,
        spawnedBySessionId: selectedSession.value?.spawnedBySessionId ?? remoteSession.value?.spawnedBySessionId ?? null,
        spawnKind: selectedSession.value?.spawnKind ?? remoteSession.value?.spawnKind ?? null,
        lineageDetachedAt: selectedSession.value
          ? selectedSession.value.lineageDetachedAt ?? null
          : remoteSession.value?.lineageDetachedAt ?? null,
      });
      return isDelegatedSession.value ? null : link;
    });

    const parentSessionLabel = computed(() => {
      return parentSession.value?.session.title?.trim() || "Parent session";
    });

    async function handleBackToParent(): Promise<void> {
      const parentSessionId = search.value.parentSessionId ?? selectedSession.value?.parentSessionId ?? remoteSession.value?.parentSessionId ?? null;
      if (!parentSessionId) {
        return;
      }

      if (parentSession.value?.instanceId) {
        await navigate({
          to: "/sessions/$id",
          params: { id: parentSessionId },
          search: { instanceId: parentSession.value.instanceId, parentSessionId: undefined },
        });
        return;
      }

      await navigate({
        to: "/sessions/$id",
        params: { id: parentSessionId },
        search: { instanceId: undefined, parentSessionId: undefined },
      });
    }

    const isArchived = computed(() => {
      return (
        optimisticSessionState.value?.retentionStatus
        ?? sessionStateOverride.value?.retentionStatus
        ?? selectedSession.value?.retentionStatus
        ?? remoteSession.value?.retentionStatus
      ) === "archived";
    });

    const effectiveActionCapabilities = computed(() => selectedSession.value?.capabilities ?? remoteSession.value?.capabilities);
    const isComposerDisabled = computed(() => {
      const capabilities = effectiveActionCapabilities.value;
      if (isArchived.value) {
        return true;
      }

      // A session that isn't running wakes on its next prompt.
      return capabilities ? !capabilities.canPrompt : effectiveLifecycleStatus.value === "error";
    });

    // A subagent's session its harness can't prompt (Claude Code) gets a line pointing at the parent, not a composer.
    const isReadOnlySubagent = computed(() => {
      return isDelegatedSession.value
        && !isArchived.value
        && effectiveActionCapabilities.value?.canPrompt === false;
    });

    const fallbackCanAbort = computed(() => effectiveLifecycleStatus.value === "running" && isActiveActivityStatus(effectiveActivityStatus.value));
    // Matches SessionCapabilitiesResolver: any session that isn't archived yet can be.
    const fallbackCanArchive = computed(() => !isArchived.value);
    const canAbort = computed(() => effectiveActionCapabilities.value?.canAbort ?? fallbackCanAbort.value);
    // Capabilities are resolved when the list loads, so the retention state wins once it changes here.
    const canArchive = computed(() => !isArchived.value && (effectiveActionCapabilities.value?.canArchive ?? fallbackCanArchive.value));
    const canRestore = computed(() => isArchived.value);
    const canFork = computed(() => effectiveActionCapabilities.value?.canFork ?? true);
    // An archived session has no Fork; on a harness that can't copy a conversation it shows off, with why.
    const forkDisabledReason = computed(() => isArchived.value || canFork.value
      ? null
      : effectiveActionCapabilities.value?.forkDisabledReason ?? "This session can't be forked.");
    const canDelete = computed(() => effectiveActionCapabilities.value?.canDelete ?? true);
    const isAnyActionPending = computed(() => isAborting.value
      || isRestoring.value
      || isDeleting.value
      || isRenaming.value);
    const actionErrors = computed(() => [
      abortError.value,
      deleteError.value,
      renameError.value,
    ].filter((message): message is string => Boolean(message)));

    const { hasPendingPrompts, sentPrompts } = useSentPrompts(params.value.id);

    const effectiveLifecycleStatus = computed(() => {
      return normalizeLifecycleStatus(
        optimisticSessionState.value?.lifecycleStatus
        ?? optimisticSessionState.value?.sessionStatus
        ?? sessionStateOverride.value?.lifecycleStatus
        ?? sessionStateOverride.value?.sessionStatus
        ?? selectedSession.value?.lifecycleStatus
        ?? selectedSession.value?.sessionStatus
        ?? remoteSession.value?.lifecycleStatus
        ?? remoteSession.value?.status,
      ) ?? "running";
    });

    const effectiveActivityStatus = computed<SessionActivityStatus>(() => {
      if (
        optimisticWorking.value
        || sentPrompts.value.length > 0
        || hasPendingPrompts.value
        || isActiveActivityStatus(optimisticSessionState.value?.activityStatus)
        || isActiveActivityStatus(sessionStateOverride.value?.activityStatus)
        || isActiveActivityStatus(selectedSession.value?.activityStatus ?? remoteSession.value?.activityStatus)
      ) {
        return "busy";
      }

      return normalizeActivityStatus(
        optimisticSessionState.value?.activityStatus
          ?? sessionStateOverride.value?.activityStatus
          ?? selectedSession.value?.activityStatus
          ?? remoteSession.value?.activityStatus,
      ) ?? "idle";
    });

    // A retrying session counts as busy above. The header names the retry, so it
    // gets the raw value.
    const headerActivityStatus = computed(() =>
      selectedSession.value?.activityStatus === "retry" ? "retry" : effectiveActivityStatus.value,
    );

    watch(
      [effectiveActivityStatus, effectiveLifecycleStatus],
      ([nextActivityStatus, nextLifecycleStatus], [previousActivityStatus, previousLifecycleStatus]) => {
        const wasActive = isDiffStalingStatus(previousActivityStatus, previousLifecycleStatus);
        const isActive = isDiffStalingStatus(nextActivityStatus, nextLifecycleStatus);

        if (isActive) {
          diffState.markStale();
          return;
        }

        if (wasActive && diffState.isStale.value && params.value.id && instanceId.value && !diffState.isLoading.value) {
          void diffState.fetchDiffs();
        }
      },
    );

    watch(
      () => [selectedSession.value?.activityStatus, hasPendingPrompts.value, sentPrompts.value.length] as const,
      ([nextActivityStatus, nextHasPendingPrompts, nextSentPromptCount]) => {
        if (isActiveActivityStatus(nextActivityStatus) || nextHasPendingPrompts || nextSentPromptCount > 0) {
          return;
        }

        optimisticWorking.value = false;
      },
      { immediate: true },
    );

    function handlePromptSent(): void {
      // sendPrompt already counted this prompt as pending; counting it again here left
      // one pending forever, so the header stayed on "Working" after the reply.
      optimisticWorking.value = true;
      sessionsStore.patchSession(params.value.id, {
        activityStatus: "busy",
        lifecycleStatus: "running",
        sessionStatus: "active",
      });
    }

    function handleSessionStateChanged(nextState: {
      activityStatus?: string | null;
      lifecycleStatus?: string | null;
      retentionStatus?: string | null;
      sessionStatus?: string | null;
    }): void {
      optimisticSessionState.value = {
        ...optimisticSessionState.value,
        ...nextState,
      };
    }

    function refreshRemoteSession(): void {
      // Reuse the existing optimistic/store state for immediate UI updates. The
      // websocket/session-list refresh will reconcile detailed values.
    }

    async function handleAbort(): Promise<void> {
      if (!params.value.id || !instanceId.value || !canAbort.value) {
        return;
      }

      try {
        await abortSession(params.value.id);
        refreshRemoteSession();
      } catch {
        // Error is exposed inline by the action toolbar.
      }
    }

    function handleFork(): void {
      if (!params.value.id || !canFork.value) {
        return;
      }

      isForkDialogOpen.value = true;
    }

    function handleDelete(): void {
      if (!params.value.id || !instanceId.value || !canDelete.value) {
        return;
      }

      isDeleteDialogOpen.value = true;
    }

    async function handleDeleteConfirmed(): Promise<void> {
      if (!params.value.id || !instanceId.value || !canDelete.value) {
        return;
      }

      try {
        await deleteSession(params.value.id, instanceId.value);
        isDeleteDialogOpen.value = false;
        await navigate({ to: "/" });
      } catch {
        // Error is exposed inline by the action toolbar.
      }
    }

    async function handleRename(proposedTitle: string): Promise<void> {
      if (!params.value.id) {
        return;
      }

      try {
        await renameSession(params.value.id, proposedTitle, () => {
          if (remoteSession.value) {
            remoteSession.value = {
              ...remoteSession.value,
              title: proposedTitle,
            };
          }
        });
      } catch {
        // Error is exposed inline by the action toolbar.
      }
    }

    function handleArchive(): void {
      if (!params.value.id || !canArchive.value) {
        return;
      }

      // Sent after the undo window; the banner shows once it is.
      archiveQueue.archive([params.value.id]);
    }

    async function handleRestore(): Promise<void> {
      if (!params.value.id || !canRestore.value) {
        return;
      }

      isRestoring.value = true;
      try {
        await archiveQueue.restore(params.value.id);
        if (optimisticSessionState.value?.retentionStatus) {
          handleSessionStateChanged({ retentionStatus: "active" });
        }
      } catch {
        // The archive queue shows the error.
      } finally {
        isRestoring.value = false;
      }
    }

    function handleFilesChangedFileSelected(file: { file: string }): void {
      selectedChangedFile.value = file;
    }

    function leaveMissingSession(): void {
      void navigate({ to: "/" });
    }

    function retryFilesChanged(): void {
      void diffState.fetchDiffs();
    }

    return () => sessionMissing.value ? (
      <div
        data-testid="session-not-found"
        class="flex h-full flex-col items-center justify-center gap-2 px-6 text-center"
      >
        <h2 class="text-base font-semibold text-foreground">Session not found</h2>
        <p class="max-w-sm text-sm text-muted-foreground">
          It may have been deleted, or the link is from another Fleet.
        </p>
        <button
          type="button"
          class="mt-2 inline-flex h-8 items-center justify-center gap-2 rounded-md border bg-background px-3 text-sm font-medium hover:bg-accent hover:text-accent-foreground dark:border-input dark:bg-input/30 dark:hover:bg-input/50"
          onClick={leaveMissingSession}
        >
          <ArrowLeft class="h-4 w-4" />
          Back to sessions
        </button>
      </div>
    ) : (
      <div
        style={{
          display: "flex",
          height: "100%",
          minHeight: 0,
          flexDirection: "column",
          overflow: "hidden",
        }}
      >
        <div
          style={{
            flexShrink: 0,
            padding: "0",
            display: "flex",
            flexDirection: "column",
          }}
        >
          <SessionDetailHeader
            id={params.value.id}
            instanceId={instanceId.value}
            origin={selectedSession.value?.origin ?? null}
            title={selectedSession.value?.session.title ?? remoteSession.value?.title}
            projectName={selectedSession.value?.projectName ?? null}
            harnessType={selectedSession.value?.harnessType ?? remoteSession.value?.harnessType ?? null}
            harnessProfileName={remoteSession.value?.harnessProfileName ?? null}
            directory={selectedSession.value?.workspaceDirectory ?? remoteSession.value?.workspaceDirectory ?? null}
            branch={selectedSession.value?.branch ?? remoteSession.value?.branch ?? null}
            activityStatus={headerActivityStatus.value}
            lifecycleStatus={effectiveLifecycleStatus.value}
            retryAttempt={selectedSession.value?.retryAttempt ?? null}
            retryMaxAttempts={selectedSession.value?.retryMaxAttempts ?? null}
            retryMessage={selectedSession.value?.retryMessage ?? null}
            retryNext={selectedSession.value?.retryNext ?? null}
            retentionStatus={optimisticSessionState.value?.retentionStatus ?? sessionStateOverride.value?.retentionStatus ?? selectedSession.value?.retentionStatus ?? remoteSession.value?.retentionStatus}
            totalTokens={selectedSession.value?.totalTokens ?? remoteSession.value?.totalTokens ?? null}
            totalCost={selectedSession.value?.totalCost ?? remoteSession.value?.totalCost ?? null}
            tags={selectedSession.value?.tags ?? []}
            lineageParentId={startedBy.value?.parentId ?? null}
            lineageKind={startedBy.value?.kind ?? null}
            editingTitle={isEditingTitle.value}
            renameDisabled={isArchived.value}
            canRestore={canRestore.value}
            isRestoring={isRestoring.value}
            onUpdate:editingTitle={(editing: boolean) => { isEditingTitle.value = editing; }}
            onRename={(title: string) => void handleRename(title)}
            onRestore={() => void handleRestore()}
            sessionStateChanged={handleSessionStateChanged}
          >
            {{
              actions: () => (
                <>
                <TerminalToggleButton sessionId={params.value.id} />
                <RightPanelSheetButton />
                <SessionActionToolbar
                  canAbort={canAbort.value}
                  canArchive={canArchive.value}
                  canRestore={canRestore.value}
                  canRename={!isArchived.value}
                  canFork={canFork.value}
                  forkDisabledReason={forkDisabledReason.value}
                  canDelete={canDelete.value}
                  isPending={isAnyActionPending.value}
                  isAborting={isAborting.value}
                  isRenaming={isRenaming.value}
                  isDeleting={isDeleting.value}
                  hasSession={Boolean(params.value.id)}
                  hasInstance={Boolean(instanceId.value)}
                  errors={actionErrors.value}
                  onAbort={() => void handleAbort()}
                  onFork={handleFork}
                  onRename={() => { isEditingTitle.value = true; }}
                  onDelete={handleDelete}
                  onArchive={handleArchive}
                  onRestore={() => void handleRestore()}
                />
                </>
              ),
            }}
          </SessionDetailHeader>
          <WorkflowStepper sessionId={params.value.id} />
          {isDelegatedSession.value ? (
            <div
              style={{
                display: "flex",
                flexWrap: "wrap",
                alignItems: "center",
                justifyContent: "space-between",
                gap: "0.75rem",
                border: "1px solid color-mix(in srgb, var(--border) 82%, transparent)",
                borderTop: 0,
                borderLeft: 0,
                borderRight: 0,
                borderRadius: 0,
                background: "color-mix(in srgb, var(--muted) 32%, transparent)",
                padding: "0.75rem 0.875rem",
              }}
            >
              <div
                style={{
                  display: "flex",
                  minWidth: 0,
                  alignItems: "center",
                  gap: "0.75rem",
                }}
              >
                <div
                  style={{
                    display: "flex",
                    height: "2rem",
                    width: "2rem",
                    alignItems: "center",
                    justifyContent: "center",
                    borderRadius: "999px",
                    background: "color-mix(in srgb, var(--primary, #6366f1) 16%, transparent)",
                    color: "color-mix(in srgb, var(--primary, #6366f1) 70%, white 30%)",
                    flexShrink: 0,
                  }}
                >
                  <Bot size={16} aria-hidden="true" />
                </div>
                <div style={{ display: "flex", minWidth: 0, flexDirection: "column", gap: "0.125rem" }}>
                  <span
                    style={{
                      fontSize: "0.72rem",
                      fontWeight: 700,
                      letterSpacing: "0.08em",
                      textTransform: "uppercase",
                      color: "var(--muted-foreground, var(--muted))",
                    }}
                  >
                    Delegated subagent session
                  </span>
                  {parentSessionHref.value ? (
                    <a
                      href={parentSessionHref.value}
                      style={{
                        overflow: "hidden",
                        textOverflow: "ellipsis",
                        whiteSpace: "nowrap",
                        fontSize: "0.95rem",
                        fontWeight: 600,
                        color: "var(--foreground, var(--text))",
                        textDecoration: "none",
                      }}
                      title={parentSessionLabel.value}
                      onClick={(event) => {
                        event.preventDefault();
                        void handleBackToParent();
                      }}
                    >
                      {`From ${parentSessionLabel.value}`}
                    </a>
                  ) : (
                    <span
                      style={{
                        overflow: "hidden",
                        textOverflow: "ellipsis",
                        whiteSpace: "nowrap",
                        fontSize: "0.95rem",
                        fontWeight: 600,
                        color: "var(--foreground, var(--text))",
                      }}
                      title={parentSessionLabel.value}
                    >
                      Opened from a parent session
                    </span>
                  )}
                </div>
              </div>

              {isDelegatedSession.value ? (
                <button
                  type="button"
                  class="inline-flex h-8 items-center justify-center gap-2 border bg-background px-3 text-sm font-medium shadow-xs transition-all hover:bg-accent hover:text-accent-foreground dark:border-input dark:bg-input/30 dark:hover:bg-input/50"
                  onClick={() => void handleBackToParent()}
                >
                  <ArrowLeft class="h-4 w-4" />
                  Back to parent
                </button>
              ) : null}
            </div>
          ) : null}
        </div>
        {viewMode.value === "chat" ? (
          <>
            <ActivityStream key={`${params.value.id}-${instanceId.value}`} sessionId={params.value.id} />
            <WorkflowRunCard sessionId={params.value.id} />
            <WorkflowFinishBar sessionId={params.value.id} />
            <RecapLine recap={recap.value} />
            <SideConversationPanel sessionId={params.value.id} />
            {isReadOnlySubagent.value ? (
              <SubagentPromptLine
                sessionId={params.value.id}
                parentTitle={parentSession.value?.session.title?.trim() || null}
                reason={effectiveActionCapabilities.value?.promptDisabledReason ?? null}
                onBack={() => void handleBackToParent()}
              />
            ) : (
              <Composer
                ref={composerRef}
                sessionId={params.value.id}
                instanceId={instanceId.value}
                disabled={isComposerDisabled.value}
                onPromptSent={handlePromptSent}
              />
            )}
          </>
        ) : (
          <FilesChangedView
            selectedFile={selectedFilesChangedViewFile.value}
            onClose={() => void setViewMode("chat")}
            onSelect={handleFilesChangedFileSelected}
            onRetry={retryFilesChanged}
            style={{
              flex: 1,
              minHeight: 0,
            }}
          />
        )}
        <TerminalDrawer
          key={params.value.id}
          sessionId={params.value.id}
          directory={selectedSession.value?.workspaceDirectory ?? remoteSession.value?.workspaceDirectory ?? null}
        />
        <ConfirmDeleteSessionDialog
          v-model:open={isDeleteDialogOpen.value}
          isDeleting={isDeleting.value}
          sessionTitle={selectedSession.value?.session.title ?? remoteSession.value?.title ?? "Untitled session"}
          onConfirm={() => void handleDeleteConfirmed()}
        />
        <DiffsTray
          open={isDiffsTrayOpen.value}
          selectedFile={selectedFilesChangedViewFile.value}
          onUpdate:open={(value: boolean) => {
            isDiffsTrayOpen.value = value;
          }}
          onSelect={handleFilesChangedFileSelected}
          onRetry={retryFilesChanged}
        />
        {canFork.value ? (
          <ForkSessionDialog
            open={isForkDialogOpen.value}
            sessionId={params.value.id ?? ""}
            sourceTitle={selectedSession.value?.session.title ?? remoteSession.value?.title ?? "Untitled session"}
            onUpdate:open={(value: boolean) => {
              isForkDialogOpen.value = value;
            }}
          />
        ) : null}
        </div>
    );
  },
});

export const Route = createFileRoute("/sessions/$id")({
  validateSearch: (search: Record<string, unknown>) => ({
    instanceId: typeof search.instanceId === "string" ? search.instanceId : undefined,
    parentSessionId: typeof search.parentSessionId === "string" ? search.parentSessionId : undefined,
    ...(search.view === "files" ? { view: "files" as const } : {}),
  }),
  component: SessionDetailPage,
});
