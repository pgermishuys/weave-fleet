import { defineStore } from "pinia";
import { computed, shallowRef } from "vue";
import { useArchiveSession, useUnarchiveSession } from "@/composables/use-session-actions";
import { useSessionsStore } from "@/stores/sessions";

/** How long an archive waits for Undo before it reaches the server. */
export const ARCHIVE_UNDO_MS = 6000;

interface PendingArchive {
  ids: readonly string[];
  message: string;
  timer: ReturnType<typeof setTimeout>;
}

/**
 * Archives wait here before they are sent, so Undo only has to cancel a timer. Closing the tab
 * inside the wait leaves the sessions active, which is the safe way for it to fail.
 */
export const useArchiveQueueStore = defineStore("archive-queue", () => {
  const sessionsStore = useSessionsStore();
  const { archiveSession } = useArchiveSession();
  const { unarchiveSession } = useUnarchiveSession();

  const pending = shallowRef<PendingArchive | null>(null);
  const error = shallowRef<string | null>(null);
  const pendingIds = computed(() => new Set(pending.value?.ids ?? []));

  function titleOf(sessionId: string): string {
    const title = sessionsStore.sessions.find((item) => item.session.id === sessionId)?.session.title?.trim();
    return title || "Untitled session";
  }

  function setRetention(sessionId: string, retentionStatus: "active" | "archived"): void {
    sessionsStore.patchSession(sessionId, { retentionStatus });
    sessionsStore.patchSessionStateOverride(sessionId, { retentionStatus });
  }

  /** Hides the sessions now and archives them once the undo window closes. */
  function archive(sessionIds: readonly string[]): void {
    const ids = [...new Set(sessionIds)];
    if (ids.length === 0) {
      return;
    }

    // A second archive inside the window sends the first one right away.
    void flush();
    error.value = null;
    pending.value = {
      ids,
      message: ids.length === 1 ? `Archived "${titleOf(ids[0])}"` : `Archived ${ids.length} sessions`,
      timer: setTimeout(() => void flush(), ARCHIVE_UNDO_MS),
    };
  }

  function undo(): void {
    if (!pending.value) {
      return;
    }

    clearTimeout(pending.value.timer);
    pending.value = null;
  }

  async function flush(): Promise<void> {
    const current = pending.value;
    if (!current) {
      return;
    }

    clearTimeout(current.timer);
    pending.value = null;
    const failed: string[] = [];
    await Promise.all(current.ids.map(async (sessionId) => {
      try {
        await archiveSession(sessionId);
        setRetention(sessionId, "archived");
      } catch {
        failed.push(sessionId);
      }
    }));

    if (failed.length > 0) {
      error.value = failed.length === 1
        ? `Couldn't archive "${titleOf(failed[0])}".`
        : `Couldn't archive ${failed.length} sessions.`;
    }
  }

  async function restore(sessionId: string): Promise<void> {
    error.value = null;
    try {
      await unarchiveSession(sessionId);
      setRetention(sessionId, "active");
    } catch (restoreError) {
      error.value = restoreError instanceof Error ? restoreError.message : "Couldn't restore the session.";
      throw restoreError;
    }
  }

  function dismissError(): void {
    error.value = null;
  }

  return {
    pending,
    pendingIds,
    error,
    archive,
    undo,
    flush,
    restore,
    dismissError,
  };
});
