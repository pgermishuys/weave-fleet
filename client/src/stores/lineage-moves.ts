import { defineStore } from "pinia";
import { shallowRef } from "vue";
import { updateSessionLineage } from "@/composables/use-session-actions";
import { liveTarget, type MachineTarget } from "@/lib/machine-target";
import { lineageOriginOf } from "@/lib/session-lineage";
import { ARCHIVE_UNDO_MS } from "@/stores/archive-queue";
import { useSessionListOn } from "@/stores/session-list-on";

/** How long Undo stays offered after a session moves out of its parent or back under it. */
export const LINEAGE_UNDO_MS = ARCHIVE_UNDO_MS;

interface PendingLineageMove {
  sessionId: string;
  /** The machine it moved on: Undo goes there too. */
  machine: MachineTarget;
  /** Whether it moved out (true) or back under its parent (false); Undo does the other. */
  detached: boolean;
  message: string;
  timer: ReturnType<typeof setTimeout>;
}

/**
 * Moving a fork or a session another session started out of its parent, or back under it. The list changes at once and
 * the server is told right away; Undo (the same toast as archiving) moves it back. See *Lineage* in
 * `docs/background-work-and-lineage.md`. Each call takes the machine the session is on (the live one when left out).
 */
export const useLineageMovesStore = defineStore("lineage-moves", () => {
  const listOn = useSessionListOn();

  const pending = shallowRef<PendingLineageMove | null>(null);
  const error = shallowRef<string | null>(null);

  function titleOf(machine: MachineTarget, sessionId: string | undefined): string | null {
    if (!sessionId) return null;
    return listOn(machine).find(sessionId)?.session.title?.trim() || null;
  }

  function messageFor(machine: MachineTarget, sessionId: string, detached: boolean): string {
    const item = listOn(machine).find(sessionId);
    const title = titleOf(machine, sessionId) ?? "Untitled session";
    const parent = item ? titleOf(machine, lineageOriginOf(item)?.parentId) : null;
    if (detached) return parent ? `Moved "${title}" out of "${parent}"` : `Moved "${title}" out of its parent`;
    return parent ? `Moved "${title}" back under "${parent}"` : `Moved "${title}" back under its parent`;
  }

  /** Changes the list now and tells the server; puts the list back and says so when the server refuses. */
  async function apply(machine: MachineTarget, sessionId: string, detached: boolean): Promise<boolean> {
    const list = listOn(machine);
    const previous = list.find(sessionId)?.lineageDetachedAt ?? null;
    list.patch(sessionId, { lineageDetachedAt: detached ? new Date().toISOString() : null });
    try {
      await updateSessionLineage(machine, sessionId, detached);
      list.settled();
      return true;
    } catch (moveError) {
      list.patch(sessionId, { lineageDetachedAt: previous });
      error.value = moveError instanceof Error ? moveError.message : "Couldn't move the session.";
      return false;
    }
  }

  function clearPending(): void {
    if (pending.value) clearTimeout(pending.value.timer);
    pending.value = null;
  }

  async function move(machine: MachineTarget, sessionId: string, detached: boolean): Promise<void> {
    clearPending();
    error.value = null;
    const message = messageFor(machine, sessionId, detached);
    if (!(await apply(machine, sessionId, detached))) return;
    pending.value = { sessionId, machine, detached, message, timer: setTimeout(clearPending, LINEAGE_UNDO_MS) };
  }

  /** Moves a fork or a started session out of the session it came from, so it stands on its own. */
  function moveOut(sessionId: string, machine: MachineTarget = liveTarget()): Promise<void> {
    return move(machine, sessionId, true);
  }

  /** Puts a session the user moved out back under the session it came from. */
  function moveBack(sessionId: string, machine: MachineTarget = liveTarget()): Promise<void> {
    return move(machine, sessionId, false);
  }

  async function undo(): Promise<void> {
    const current = pending.value;
    if (!current) return;
    clearPending();
    await apply(current.machine, current.sessionId, !current.detached);
  }

  function dismissError(): void {
    error.value = null;
  }

  return { pending, error, moveOut, moveBack, undo, dismissError };
});
