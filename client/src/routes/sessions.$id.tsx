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
import { useSessionDetail } from "@/composables/use-session-detail";
import { useSessionDetailActions } from "@/composables/use-session-detail-actions";
import { useSentPrompts } from "@/composables/use-send-prompt";
import { provideSessionDiffsContext } from "@/composables/use-session-diffs-context";
import { useSessionRecap } from "@/composables/use-session-recap";
import { useSessionTerminals } from "@/composables/use-session-terminals";
import { lineageOf } from "@/lib/session-lineage";
import {
  isActiveActivityStatus,
  isDiffStalingStatus,
  normalizeActivityStatus,
  normalizeLifecycleStatus,
} from "@/lib/session-detail";
import type { SessionActivityStatus } from "@/lib/types";
import { useSessionsStore } from "@/stores/sessions";

type ComposerInstance = ComponentPublicInstance & {
  focusPrompt: () => void;
};

type SessionViewMode = "chat" | "files-changed";


const SessionDetailPage = defineComponent({
  name: "SessionDetailPage",
  setup(_props, { expose }) {
    const params = Route.useParams();
    useSessionTerminals(() => params.value.id);
    const recap = useSessionRecap(() => params.value.id);
    const search = Route.useSearch();
    const navigate = Route.useNavigate();
    const sessionsStore = useSessionsStore();
    const { sessionStateOverrides } = storeToRefs(sessionsStore);
    const { remoteSession, sessionMissing } = useSessionDetail(
      () => params.value.id,
      () => search.value.instanceId,
    );
    const composerRef = shallowRef<ComposerInstance | null>(null);
    const viewMode = shallowRef<SessionViewMode>(search.value.view === "files" ? "files-changed" : "chat");
    const selectedChangedFile = shallowRef<{ file: string } | null>(null);
    const optimisticWorking = shallowRef(false);
    const isDiffsTrayOpen = shallowRef(false);
    const optimisticSessionState = shallowRef<{
      activityStatus?: string | null;
      lifecycleStatus?: string | null;
      retentionStatus?: string | null;
      sessionStatus?: string | null;
    } | null>(null);

    const isEditingTitle = shallowRef(false);

    const selectedSession = computed(() => {
      return sessionsStore.sessionById(params.value.id);
    });

    const sessionStateOverride = computed(() => {
      return sessionStateOverrides.value[params.value.id] ?? null;
    });

    // The detail loads in useSessionDetail; this is the page's side of a new id.
    watch(
      () => params.value.id,
      (sessionId, _previousSessionId, onCleanup) => {
        isEditingTitle.value = false;

        if (!sessionId) {
          return;
        }

        let cancelled = false;
        onCleanup(() => {
          cancelled = true;
        });

        void nextTick(() => {
          if (!cancelled) {
            composerRef.value?.focusPrompt();
          }
        });
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

    const {
      isAborting,
      isDeleting,
      isRenaming,
      isRestoring,
      isDeleteDialogOpen,
      isForkDialogOpen,
      isAnyActionPending,
      actionErrors,
      handleAbort,
      handleFork,
      handleDelete,
      handleDeleteConfirmed,
      handleRename,
      handleArchive,
      handleRestore,
    } = useSessionDetailActions({
      sessionId: () => params.value.id,
      instanceId: () => instanceId.value,
      canAbort: () => canAbort.value,
      canFork: () => canFork.value,
      canDelete: () => canDelete.value,
      canArchive: () => canArchive.value,
      canRestore: () => canRestore.value,
      remoteSession,
      optimisticRetentionStatus: () => optimisticSessionState.value?.retentionStatus,
      onSessionStateChanged: handleSessionStateChanged,
      goHome: () => navigate({ to: "/" }),
    });

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
