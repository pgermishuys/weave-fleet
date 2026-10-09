import { shallowRef, watch, type ShallowRef } from "vue";
import { apiFetchOn } from "@/lib/api-client";
import { useMachineTarget } from "@/lib/machine-target";
import { buildSessionListItem, normalizeSessionDetailResponse, type SessionDetailResponse } from "@/lib/session-detail";
import { dispatchSessionUpsert } from "@/lib/session-sync";
import { useSessionsStore } from "@/stores/sessions";

export interface SessionDetail {
  /** What the server last sent for the open session; null until it answers (and while another id loads). */
  remoteSession: ShallowRef<SessionDetailResponse | null>;
  /** The server has no session under this id (a stale link, or one deleted elsewhere). */
  sessionMissing: ShallowRef<boolean>;
}

/**
 * Loads the open session's detail from the machine the page targets, files it in the sessions store (the live
 * machine's list, or `elsewhere` for another machine) and marks the session active. Reloads when the id changes;
 * a request still in flight for the old id is aborted.
 */
export function useSessionDetail(
  sessionId: () => string,
  searchInstanceId: () => string | undefined,
): SessionDetail {
  const machine = useMachineTarget();
  const sessionsStore = useSessionsStore();
  const remoteSession = shallowRef<SessionDetailResponse | null>(null);
  const sessionMissing = shallowRef(false);

  watch(
    sessionId,
    async (id, _previousId, onCleanup) => {
      sessionsStore.setActiveSessionId(id ?? null);
      remoteSession.value = null;
      sessionMissing.value = false;

      if (!id) {
        return;
      }

      const abortController = new AbortController();
      onCleanup(() => {
        abortController.abort();
      });

      try {
        const response = await apiFetchOn(machine.connection, `/api/sessions/${encodeURIComponent(id)}`, {
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

        const nextSession = buildSessionListItem(
          id,
          nextRemoteSession,
          sessionsStore.sessionById(id),
          searchInstanceId(),
        );

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

  return { remoteSession, sessionMissing };
}
