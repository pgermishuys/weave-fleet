import { defineStore } from "pinia";
import { shallowRef } from "vue";
import { api, type SessionListItem } from "@/api/client";
import { pinSession, unpinSession } from "@/composables/use-session-actions";
import { liveTarget, type MachineTarget } from "@/lib/machine-target";
import { pinOrderFor, pinnedNeighbourFor } from "@/lib/session-pins";
import { useSessionListOn } from "@/stores/session-list-on";
import { useSessionsStore } from "@/stores/sessions";

/**
 * Pinning sessions in the Pinned group above the projects, and putting them in order. The list changes at once and the
 * server is told right away; when the server refuses, the list goes back and the error shows in the archive toast.
 * Each call takes the machine the session is on (the live one when left out), and changes that machine's list.
 */
export const useSessionPinsStore = defineStore("session-pins", () => {
  const sessionsStore = useSessionsStore();
  const listOn = useSessionListOn();

  const error = shallowRef<string | null>(null);

  /** Pins the session just before `beforeId`, or at the end of the Pinned group; moves it there when it's pinned already. */
  async function pin(sessionId: string, beforeId: string | null = null, machine: MachineTarget = liveTarget()): Promise<void> {
    const list = listOn(machine);
    const item = list.find(sessionId);
    if (!item || beforeId === sessionId) return;
    error.value = null;
    const previous = item.pinOrder ?? null;
    const expected = pinOrderFor(list.sessions, sessionId, beforeId);
    list.patch(sessionId, { pinOrder: expected });
    try {
      const order = await pinSession(machine, sessionId, beforeId);
      list.patch(sessionId, { pinOrder: order });
      // The server numbered every pin again (the gap got too small): read the new numbers.
      if (order !== expected && machine.isLive) await refreshPinOrders();
      list.settled();
    } catch (pinError) {
      list.patch(sessionId, { pinOrder: previous });
      error.value = pinError instanceof Error ? pinError.message : "Couldn't pin the session.";
    }
  }

  /** Unpins the session: it goes back to its project, newest first. */
  async function unpin(sessionId: string, machine: MachineTarget = liveTarget()): Promise<void> {
    const list = listOn(machine);
    const item = list.find(sessionId);
    if (!item) return;
    error.value = null;
    const previous = item.pinOrder ?? null;
    list.patch(sessionId, { pinOrder: null });
    try {
      await unpinSession(machine, sessionId);
      list.settled();
    } catch (unpinError) {
      list.patch(sessionId, { pinOrder: previous });
      error.value = unpinError instanceof Error ? unpinError.message : "Couldn't unpin the session.";
    }
  }

  /** Moves a pinned session one place up (-1) or down (+1) in the Pinned group. */
  async function move(sessionId: string, delta: -1 | 1, machine: MachineTarget = liveTarget()): Promise<void> {
    const before = pinnedNeighbourFor(listOn(machine).sessions, sessionId, delta);
    if (before === undefined) return;
    await pin(sessionId, before, machine);
  }

  async function refreshPinOrders(): Promise<void> {
    const { data } = await api.GET("/api/sessions", { params: { query: { limit: sessionsStore.sessions.length || 100 } } });
    for (const listed of (data ?? []) as SessionListItem[]) {
      sessionsStore.patchSession(listed.session.id, { pinOrder: listed.pinOrder ?? null });
    }
  }

  function dismissError(): void {
    error.value = null;
  }

  return { error, pin, unpin, move, dismissError };
});
