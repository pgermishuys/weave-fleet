import { computed, shallowRef, type ShallowRef } from "vue";
import { confirmAction } from "@/components/ui/confirm";
import {
  useAbortSession,
  useDeleteSession,
  useRenameSession,
} from "@/composables/use-session-actions";
import type { SessionDetailResponse } from "@/lib/session-detail";
import { useArchiveQueueStore } from "@/stores/archive-queue";

export interface SessionDetailActionsInput {
  sessionId: () => string;
  instanceId: () => string | undefined;
  canAbort: () => boolean;
  canFork: () => boolean;
  canDelete: () => boolean;
  canArchive: () => boolean;
  canRestore: () => boolean;
  /** The title the delete confirm names. */
  sessionTitle: () => string;
  /** The fetched detail; a rename updates its title. */
  remoteSession: ShallowRef<SessionDetailResponse | null>;
  /** The retention state the page holds optimistically, if any (a restore clears it). */
  optimisticRetentionStatus: () => string | null | undefined;
  /** Tells the page the session's state changed here. */
  onSessionStateChanged: (state: { retentionStatus: string }) => void;
  /** Leaves the page for the sessions list, once the session is deleted. */
  goHome: () => Promise<unknown> | unknown;
}

/**
 * What the toolbar and header can do to the open session: abort, fork, delete, rename, archive, restore.
 * Each checks its capability first and keeps its failure inline (the toolbar shows the errors).
 */
export function useSessionDetailActions(input: SessionDetailActionsInput) {
  const archiveQueue = useArchiveQueueStore();
  const { abortSession, isAborting, error: abortError } = useAbortSession();
  const { deleteSession, isDeleting, error: deleteError } = useDeleteSession();
  const { renameSession, isLoading: isRenaming, error: renameError } = useRenameSession();
  const isRestoring = shallowRef(false);
  const isForkDialogOpen = shallowRef(false);

  const isAnyActionPending = computed(() => isAborting.value
    || isRestoring.value
    || isDeleting.value
    || isRenaming.value);
  const actionErrors = computed(() => [
    abortError.value,
    deleteError.value,
    renameError.value,
  ].filter((message): message is string => Boolean(message)));

  async function handleAbort(): Promise<void> {
    if (!input.sessionId() || !input.instanceId() || !input.canAbort()) {
      return;
    }

    try {
      await abortSession(input.sessionId());
      // The existing optimistic/store state updates the UI; the websocket/session-list refresh reconciles the rest.
    } catch {
      // Error is exposed inline by the action toolbar.
    }
  }

  function handleFork(): void {
    if (!input.sessionId() || !input.canFork()) {
      return;
    }

    isForkDialogOpen.value = true;
  }

  /** Asks first; the dialog stays open while the delete runs and shows why if it fails. */
  async function handleDelete(): Promise<void> {
    const sessionId = input.sessionId();
    const instanceId = input.instanceId();
    if (!sessionId || !instanceId || !input.canDelete()) {
      return;
    }

    const deleted = await confirmAction({
      title: "Delete this session?",
      subject: input.sessionTitle(),
      body: "It and its session data are deleted for good.",
      confirmLabel: "Delete session",
      busyLabel: "Deleting…",
      errorMessage: "Couldn't delete the session.",
      destructive: true,
      run: () => deleteSession(sessionId, instanceId),
    });
    if (deleted) await input.goHome();
  }

  async function handleRename(proposedTitle: string): Promise<void> {
    if (!input.sessionId()) {
      return;
    }

    try {
      await renameSession(input.sessionId(), proposedTitle, () => {
        if (input.remoteSession.value) {
          input.remoteSession.value = {
            ...input.remoteSession.value,
            title: proposedTitle,
          };
        }
      });
    } catch {
      // Error is exposed inline by the action toolbar.
    }
  }

  function handleArchive(): void {
    if (!input.sessionId() || !input.canArchive()) {
      return;
    }

    // Sent after the undo window; the banner shows once it is.
    archiveQueue.archive([input.sessionId()]);
  }

  async function handleRestore(): Promise<void> {
    if (!input.sessionId() || !input.canRestore()) {
      return;
    }

    isRestoring.value = true;
    try {
      await archiveQueue.restore(input.sessionId());
      if (input.optimisticRetentionStatus()) {
        input.onSessionStateChanged({ retentionStatus: "active" });
      }
    } catch {
      // The archive queue shows the error.
    } finally {
      isRestoring.value = false;
    }
  }

  return {
    isAborting,
    isDeleting,
    isRenaming,
    isRestoring,
    isForkDialogOpen,
    isAnyActionPending,
    actionErrors,
    handleAbort,
    handleFork,
    handleDelete,
    handleRename,
    handleArchive,
    handleRestore,
  };
}
