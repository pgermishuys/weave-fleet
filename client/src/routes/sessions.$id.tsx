import { createFileRoute } from "@tanstack/vue-router";
import { computed, defineComponent, nextTick, shallowRef, watch, type ComponentPublicInstance } from "vue";
import { ArrowLeft, Bot } from "lucide-vue-next";
import { storeToRefs } from "pinia";
import ConfirmDeleteSessionDialog from "@/components/sessions/ConfirmDeleteSessionDialog.vue";
import ActivityStream from "@/components/session/ActivityStream.vue";
import Composer from "@/components/session/Composer.vue";
import DiffsTray from "@/components/session/DiffsTray.vue";
import FilesChangedView from "@/components/session/FilesChangedView.vue";
import ForkSessionDialog from "@/components/session/ForkSessionDialog.vue";
import SessionActionToolbar from "@/components/session/SessionActionToolbar.vue";
import SessionDetailHeader from "@/components/session/SessionDetailHeader.vue";
import { useDiffs } from "@/composables/use-diffs";
import {
  useAbortSession,
  useArchiveSession,
  useDeleteSession,
  useRenameSession,
  useResumeSession,
  useTerminateSession,
} from "@/composables/use-session-actions";
import { incrementPendingPrompts, useSentPrompts } from "@/composables/use-send-prompt";
import { provideSessionDiffsContext } from "@/composables/use-session-diffs-context";
import { apiFetch } from "@/lib/api-client";
import type { SessionActionCapabilities, SessionListItem, SessionOrigin } from "@/lib/api-types";
import type { SessionActivityStatus } from "@/lib/types";
import { dispatchSessionUpsert } from "@/lib/session-sync";
import { useSessionsStore } from "@/stores/sessions";

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
  status?: string | null;
  activityStatus?: string | null;
  lifecycleStatus?: string | null;
  retentionStatus?: string | null;
  totalTokens?: number | null;
  totalCost?: number | null;
  capabilities?: SessionActionCapabilities;
  origin?: SessionOrigin | null;
  harnessType?: string | null;
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
    workspaceId: getStringField(value, "workspaceId", "WorkspaceId"),
    workspaceDirectory: getStringField(value, "workspaceDirectory", "WorkspaceDirectory"),
    workspaceDisplayName: getStringField(value, "workspaceDisplayName", "WorkspaceDisplayName"),
    sourceDirectory: getStringField(value, "sourceDirectory", "SourceDirectory"),
    isolationStrategy: getStringField(value, "isolationStrategy", "IsolationStrategy"),
    branch: getStringField(value, "branch", "Branch"),
    title: getStringField(value, "title", "Title"),
    status: getStringField(value, "status", "Status"),
    activityStatus: getStringField(value, "activityStatus", "ActivityStatus"),
    lifecycleStatus: getStringField(value, "lifecycleStatus", "LifecycleStatus"),
    retentionStatus: getStringField(value, "retentionStatus", "RetentionStatus"),
    totalTokens: getNumberField(value, "totalTokens", "TotalTokens"),
    totalCost: getNumberField(value, "totalCost", "TotalCost"),
    capabilities: (value.capabilities ?? value.Capabilities) as SessionActionCapabilities | undefined,
    origin,
    harnessType: getStringField(value, "harnessType", "HarnessType"),
  };
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
    case "waiting_input":
      return "waiting_input";
    case "idle":
      return "idle";
    default:
      return null;
  }
}

function isActiveActivityStatus(value: string | null | undefined): value is "busy" | "delegating" {
  return value === "busy" || value === "delegating";
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
    const params = Route.useParams();
    const search = Route.useSearch();
    const navigate = Route.useNavigate();
    const sessionsStore = useSessionsStore();
    const { sessions, sessionStateOverrides } = storeToRefs(sessionsStore);
    const remoteSession = shallowRef<SessionDetailResponse | null>(null);
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

    const selectedSession = computed(() => {
      return sessions.value.find((session) => session.session.id === params.value.id) ?? null;
    });

    const sessionStateOverride = computed(() => {
      return sessionStateOverrides.value[params.value.id] ?? null;
    });

    watch(
      () => params.value.id,
      async (sessionId, _previousSessionId, onCleanup) => {
        sessionsStore.setActiveSessionId(sessionId ?? null);
        remoteSession.value = null;

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
          const response = await apiFetch(`/api/sessions/${encodeURIComponent(sessionId)}`, {
            signal: abortController.signal,
          });
          if (!response.ok || abortController.signal.aborted) {
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
              time: selectedSession.value?.session.time ?? { created: 0, updated: 0 },
            },
            instanceStatus: selectedSession.value?.instanceStatus ?? "running",
            parentSessionId: nextRemoteSession.parentSessionId ?? selectedSession.value?.parentSessionId ?? null,
            sourceDirectory: nextRemoteSession.sourceDirectory ?? selectedSession.value?.sourceDirectory ?? null,
            branch: nextRemoteSession.branch ?? selectedSession.value?.branch ?? null,
            activityStatus: normalizedActivityStatus,
            lifecycleStatus: normalizedLifecycleStatus,
            retentionStatus: normalizeRetentionStatus(nextRemoteSession.retentionStatus),
            archivedAt: selectedSession.value?.archivedAt ?? null,
            typedInstanceStatus: selectedSession.value?.typedInstanceStatus ?? "running",
            isHidden: selectedSession.value?.isHidden ?? false,
            totalTokens: selectedSession.value?.totalTokens,
            totalCost: selectedSession.value?.totalCost,
            projectId: selectedSession.value?.projectId ?? null,
            projectName: selectedSession.value?.projectName ?? null,
            capabilities: nextRemoteSession.capabilities ?? selectedSession.value?.capabilities,
            origin: nextRemoteSession.origin ?? selectedSession.value?.origin ?? null,
            harnessType: nextRemoteSession.harnessType ?? selectedSession.value?.harnessType ?? null,
          } satisfies SessionListItem;

          sessionsStore.upsertSession(nextSession);
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
      () => instanceId.value,
    );
    function openDiffsTray(): void {
      if (viewMode.value !== "chat") {
        void setViewMode("chat");
      }

      isDiffsTrayOpen.value = true;

      if (!diffState.isLoading.value && (diffState.isStale.value || diffState.diffs.value.length === 0) && params.value.id && instanceId.value) {
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
      [() => params.value.id, instanceId],
      async ([sessionId, activeInstanceId]) => {
        if (sessionId && activeInstanceId) {
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
      const parentSessionId = search.value.parentSessionId ?? selectedSession.value?.parentSessionId ?? null;
      if (!parentSessionId) {
        return null;
      }

      return sessions.value.find((session) => session.session.id === parentSessionId) ?? null;
    });

    const parentSessionHref = computed(() => {
      const parentSessionId = search.value.parentSessionId ?? selectedSession.value?.parentSessionId ?? null;
      if (!parentSessionId) {
        return null;
      }

      if (!parentSession.value?.instanceId) {
        return `/sessions/${parentSessionId}`;
      }

      return `/sessions/${parentSessionId}?instanceId=${parentSession.value.instanceId}`;
    });

    const isDelegatedSession = computed(() => {
      return Boolean(search.value.parentSessionId || selectedSession.value?.parentSessionId);
    });

    const parentSessionLabel = computed(() => {
      return parentSession.value?.session.title?.trim() || "Parent session";
    });

    async function handleBackToParent(): Promise<void> {
      const parentSessionId = search.value.parentSessionId ?? selectedSession.value?.parentSessionId ?? null;
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

      return capabilities ? !capabilities.canPrompt : effectiveLifecycleStatus.value !== "running";
    });

    const fallbackCanAbort = computed(() => effectiveLifecycleStatus.value === "running" && isActiveActivityStatus(effectiveActivityStatus.value));
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
    const fallbackCanStop = computed(() => effectiveLifecycleStatus.value === "running");
    const fallbackCanArchive = computed(() => !isArchived.value && effectiveLifecycleStatus.value !== "running");
    const canAbort = computed(() => effectiveActionCapabilities.value?.canAbort ?? fallbackCanAbort.value);
    const canResume = computed(() => effectiveActionCapabilities.value?.canResume ?? fallbackCanResume.value);
    const canStop = computed(() => effectiveActionCapabilities.value?.canStop ?? fallbackCanStop.value);
    const canArchive = computed(() => effectiveActionCapabilities.value?.canArchive ?? fallbackCanArchive.value);
    const canFork = computed(() => effectiveActionCapabilities.value?.canFork ?? true);
    const canDelete = computed(() => effectiveActionCapabilities.value?.canDelete ?? true);
    const isResumingCurrentSession = computed(() => isResuming.value && resumingSessionId.value === params.value.id);
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
      optimisticWorking.value = true;
      incrementPendingPrompts(params.value.id);
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
        await abortSession(params.value.id, instanceId.value);
        refreshRemoteSession();
      } catch {
        // Error is exposed inline by the action toolbar.
      }
    }

    async function handleResume(): Promise<void> {
      if (!params.value.id || !canResume.value) {
        return;
      }

      try {
        handleSessionStateChanged({
          activityStatus: "idle",
          lifecycleStatus: "resuming",
          sessionStatus: "resuming",
        });
        sessionsStore.patchSession(params.value.id, {
          activityStatus: "idle",
          lifecycleStatus: "resuming",
          sessionStatus: "resuming",
        });

        const response = await resumeSession(params.value.id);
        await navigate({
          to: "/sessions/$id",
          params: { id: response.session.id },
          search: {
            instanceId: response.instanceId,
            parentSessionId: undefined,
          },
        });
      } catch {
        handleSessionStateChanged({
          activityStatus: "idle",
          lifecycleStatus: "stopped",
          sessionStatus: "stopped",
        });
        sessionsStore.patchSession(params.value.id, {
          activityStatus: "idle",
          lifecycleStatus: "stopped",
          sessionStatus: "stopped",
        });
      }
    }

    async function handleStop(): Promise<void> {
      if (!params.value.id || !instanceId.value || !canStop.value) {
        return;
      }

      try {
        await terminateSession(params.value.id, instanceId.value);
        handleSessionStateChanged({
          activityStatus: "idle",
          lifecycleStatus: "stopped",
          sessionStatus: "stopped",
        });
        sessionsStore.patchSession(params.value.id, {
          activityStatus: "idle",
          lifecycleStatus: "stopped",
          sessionStatus: "stopped",
        });
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

    async function handleRename(): Promise<void> {
      if (!params.value.id) {
        return;
      }

      const currentTitle = selectedSession.value?.session.title ?? remoteSession.value?.title ?? "Untitled session";
      const proposedTitle = window.prompt("Rename session", currentTitle)?.trim();
      if (!proposedTitle || proposedTitle === currentTitle) {
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

    async function handleArchive(): Promise<void> {
      if (!params.value.id || !canArchive.value) {
        return;
      }

      try {
        await archiveSession(params.value.id);
        handleSessionStateChanged({ retentionStatus: "archived" });
        sessionsStore.patchSession(params.value.id, {
          retentionStatus: "archived",
        });
      } catch {
        // Error is exposed inline by the action toolbar.
      }
    }

    function handleFilesChangedFileSelected(file: { file: string }): void {
      selectedChangedFile.value = file;
    }

    function retryFilesChanged(): void {
      void diffState.fetchDiffs();
    }

    return () => (
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
            activityStatus={effectiveActivityStatus.value}
            lifecycleStatus={effectiveLifecycleStatus.value}
            retentionStatus={optimisticSessionState.value?.retentionStatus ?? sessionStateOverride.value?.retentionStatus ?? selectedSession.value?.retentionStatus ?? remoteSession.value?.retentionStatus}
            totalTokens={selectedSession.value?.totalTokens ?? remoteSession.value?.totalTokens ?? null}
            totalCost={selectedSession.value?.totalCost ?? remoteSession.value?.totalCost ?? null}
            sessionStateChanged={handleSessionStateChanged}
          >
            {{
              actions: () => (
                <SessionActionToolbar
                  canAbort={canAbort.value}
                  canResume={canResume.value}
                  canStop={canStop.value}
                  canArchive={canArchive.value}
                  canFork={canFork.value}
                  canDelete={canDelete.value}
                  isPending={isAnyActionPending.value}
                  isAborting={isAborting.value}
                  isResuming={isResumingCurrentSession.value}
                  isTerminating={isTerminating.value}
                  isRenaming={isRenaming.value}
                  isDeleting={isDeleting.value}
                  isArchiving={isArchiving.value}
                  hasSession={Boolean(params.value.id)}
                  hasInstance={Boolean(instanceId.value)}
                  errors={actionErrors.value}
                  onAbort={() => void handleAbort()}
                  onResume={() => void handleResume()}
                  onStop={() => void handleStop()}
                  onFork={handleFork}
                  onRename={() => void handleRename()}
                  onDelete={handleDelete}
                  onArchive={() => void handleArchive()}
                />
              ),
            }}
          </SessionDetailHeader>
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

              {search.value.parentSessionId || selectedSession.value?.parentSessionId ? (
                <button
                  type="button"
                  class="inline-flex h-8 items-center justify-center gap-2 rounded-md border bg-background px-3 text-sm font-medium shadow-xs transition-all hover:bg-accent hover:text-accent-foreground dark:border-input dark:bg-input/30 dark:hover:bg-input/50"
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
            <ActivityStream key={`${params.value.id}-${instanceId.value}`} sessionId={params.value.id} instanceId={instanceId.value} />
            <Composer
              ref={composerRef}
              sessionId={params.value.id}
              instanceId={instanceId.value}
              disabled={isComposerDisabled.value}
              onPromptSent={handlePromptSent}
            />
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
