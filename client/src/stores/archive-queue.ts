import { defineStore } from "pinia";
import { computed, shallowRef } from "vue";
import { useArchiveSession, useUnarchiveSession } from "@/composables/use-session-actions";
import type { MachineTarget } from "@/lib/machine-target";
import { useMachinesStore } from "@/stores/machines";
import { useSessionListOn } from "@/stores/session-list-on";
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
  // A session is archived on its machine: the one its row was on when the row asked, else the one it's known to be on
  // (a session opened in place on another machine).
  const machines = useMachinesStore();
  const rowMachines = new Map<string, MachineTarget>();
  const sessionMachine = (sessionId: string) => rowMachines.get(sessionId) ?? machines.sessionTarget(sessionId);
  const { archiveSession } = useArchiveSession(sessionMachine);
  const { unarchiveSession } = useUnarchiveSession(sessionMachine);

  const pending = shallowRef<PendingArchive | null>(null);
  const error = shallowRef<string | null>(null);
  // Sessions whose archive request is on its way: still hidden, because the list says active until the server has answered.
  const sending = shallowRef<ReadonlySet<string>>(new Set());
  const pendingIds = computed(() => new Set([...(pending.value?.ids ?? []), ...sending.value]));

  // Another machine's row is in that machine's list, which is read again once it has been told.
  const listOn = useSessionListOn();

  function titleOf(sessionId: string): string {
    const title = (sessionsStore.sessionById(sessionId) ?? listOn(sessionMachine(sessionId)).find(sessionId))?.session.title?.trim();
    return title || "Untitled session";
  }

  function setRetention(sessionId: string, retentionStatus: "active" | "archived"): void {
    sessionsStore.patchSession(sessionId, { retentionStatus });
    sessionsStore.patchSessionStateOverride(sessionId, { retentionStatus });
    const machine = sessionMachine(sessionId);
    if (!machine.isLive) {
      const list = listOn(machine);
      list.patch(sessionId, { retentionStatus });
      list.settled();
    }
  }

  /** Only another machine's: the live machine is the default, and which one is live can change. */
  function rememberMachine(sessionIds: readonly string[], machine: MachineTarget | undefined): void {
    if (!machine || machine.isLive) return;
    for (const sessionId of sessionIds) rowMachines.set(sessionId, machine);
  }

  /**
   * Hides the sessions now and archives them once the undo window closes, on `machine` when it's given (the machine of
   * the rows they were picked from; they're all on one).
   */
  function archive(sessionIds: readonly string[], machine?: MachineTarget): void {
    const ids = [...new Set(sessionIds)];
    if (ids.length === 0) {
      return;
    }

    // A second archive inside the window sends the first one right away.
    void flush();
    rememberMachine(ids, machine);
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
    sending.value = new Set([...sending.value, ...current.ids]);
    const failed: string[] = [];
    await Promise.all(current.ids.map(async (sessionId) => {
      try {
        await archiveSession(sessionId);
        setRetention(sessionId, "archived");
      } catch {
        failed.push(sessionId);
      } finally {
        const rest = new Set(sending.value);
        rest.delete(sessionId);
        sending.value = rest;
      }
    }));

    if (failed.length > 0) {
      error.value = failed.length === 1
        ? `Couldn't archive "${titleOf(failed[0])}".`
        : `Couldn't archive ${failed.length} sessions.`;
    }
  }

  /** Restores an archived session, on `machine` when it's given (the machine of the row it was restored from). */
  async function restore(sessionId: string, machine?: MachineTarget): Promise<void> {
    error.value = null;
    rememberMachine([sessionId], machine);
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
