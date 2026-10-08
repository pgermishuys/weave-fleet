import { defineStore } from "pinia";
import { shallowRef } from "vue";
import { api, type SessionListItem } from "@/api/client";
import { pinSession, unpinSession } from "@/composables/use-session-actions";
import { liveTarget } from "@/lib/machine-target";
import { pinOrderFor, pinnedNeighbourFor } from "@/lib/session-pins";
import { useSessionsStore } from "@/stores/sessions";

/**
 * Pinning sessions in the Pinned group above the projects, and putting them in order. The list changes at once and the
 * server is told right away; when the server refuses, the list goes back and the error shows in the archive toast.
 * The session list is the live machine's, so pins go there.
 */
export const useSessionPinsStore = defineStore("session-pins", () => {
  const sessionsStore = useSessionsStore();

  const error = shallowRef<string | null>(null);

  function find(sessionId: string): SessionListItem | undefined {
    return sessionsStore.sessions.find((item) => item.session.id === sessionId);
  }

  /** Pins the session just before `beforeId`, or at the end of the Pinned group; moves it there when it's pinned already. */
  async function pin(sessionId: string, beforeId: string | null = null): Promise<void> {
    const item = find(sessionId);
    if (!item || beforeId === sessionId) return;
    error.value = null;
    const previous = item.pinOrder ?? null;
    const expected = pinOrderFor(sessionsStore.sessions, sessionId, beforeId);
    sessionsStore.patchSession(sessionId, { pinOrder: expected });
    try {
      const order = await pinSession(liveTarget(), sessionId, beforeId);
      sessionsStore.patchSession(sessionId, { pinOrder: order });
      // The server numbered every pin again (the gap got too small): read the new numbers.
      if (order !== expected) await refreshPinOrders();
    } catch (pinError) {
      sessionsStore.patchSession(sessionId, { pinOrder: previous });
      error.value = pinError instanceof Error ? pinError.message : "Couldn't pin the session.";
    }
  }

  /** Unpins the session: it goes back to its project, newest first. */
  async function unpin(sessionId: string): Promise<void> {
    const item = find(sessionId);
    if (!item) return;
    error.value = null;
    const previous = item.pinOrder ?? null;
    sessionsStore.patchSession(sessionId, { pinOrder: null });
    try {
      await unpinSession(liveTarget(), sessionId);
    } catch (unpinError) {
      sessionsStore.patchSession(sessionId, { pinOrder: previous });
      error.value = unpinError instanceof Error ? unpinError.message : "Couldn't unpin the session.";
    }
  }

  /** Moves a pinned session one place up (-1) or down (+1) in the Pinned group. */
  async function move(sessionId: string, delta: -1 | 1): Promise<void> {
    const before = pinnedNeighbourFor(sessionsStore.sessions, sessionId, delta);
    if (before === undefined) return;
    await pin(sessionId, before);
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
