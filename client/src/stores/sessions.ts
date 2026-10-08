import { defineStore } from "pinia";
import { ref, shallowRef } from "vue";
import type { SessionListItem } from "@/api/client";

type SessionStateOverride = Partial<Pick<SessionListItem, "activityStatus" | "lifecycleStatus" | "retentionStatus" | "sessionStatus">>;

/** How many sessions on other machines `elsewhere` keeps: the ones opened most recently in this page. */
const ELSEWHERE_KEPT = 20;

export const useSessionsStore = defineStore("sessions", () => {
  const sessions = ref<SessionListItem[]>([]);
  const activeSessionId = shallowRef<string | null>(null);
  const retentionStatus = shallowRef<"active" | "archived" | "all">("active");
  const sessionStateOverrides = ref<Record<string, SessionStateOverride>>({});
  /** Whether the list has been loaded from Fleet at least once (sessions upserted one by one don't make a list). */
  const listLoaded = shallowRef(false);
  /**
   * Sessions on another machine opened in this page (in place, with "Keep every machine live"), by id, with their
   * machine's key, oldest first. `sessions` is the live machine's list and never holds them.
   */
  const elsewhere = shallowRef<ReadonlyMap<string, { machineKey: string; item: SessionListItem }>>(new Map());

  /** The session's row: from the live machine's list, or from the sessions on other machines opened here. */
  function sessionById(sessionId: string | null | undefined): SessionListItem | null {
    if (!sessionId) return null;
    return sessions.value.find((item) => item.session.id === sessionId) ?? elsewhere.value.get(sessionId)?.item ?? null;
  }

  /** Keeps the row of a session on another machine (`machineKey`) that's open here. */
  function upsertElsewhere(machineKey: string, nextSession: SessionListItem): void {
    const next = new Map(elsewhere.value);
    const existing = next.get(nextSession.session.id);
    next.delete(nextSession.session.id);
    next.set(nextSession.session.id, { machineKey, item: { ...existing?.item, ...nextSession } });
    for (const id of next.keys()) {
      if (next.size <= ELSEWHERE_KEPT) break;
      next.delete(id);
    }
    elsewhere.value = next;
  }

  /** Forgets the sessions of a machine that's no longer listed. */
  function forgetElsewhere(machineKey: string): void {
    elsewhere.value = new Map([...elsewhere.value].filter(([, entry]) => entry.machineKey !== machineKey));
  }

  function setActiveSessionId(sessionId: string | null): void {
    activeSessionId.value = sessionId;
  }

  function setRetentionStatus(nextRetentionStatus: "active" | "archived" | "all"): void {
    retentionStatus.value = nextRetentionStatus;
  }

  function setSessions(nextSessions: readonly SessionListItem[]): void {
    sessions.value = [...nextSessions];
    listLoaded.value = true;
  }

  function patchSession(
    sessionId: string,
    patch: Partial<SessionListItem>,
  ): void {
    const away = elsewhere.value.get(sessionId);
    if (away) {
      elsewhere.value = new Map(elsewhere.value).set(sessionId, { ...away, item: { ...away.item, ...patch } });
      return;
    }

    if (!sessions.value.some((item) => item.session.id === sessionId)) {
      return;
    }

    sessions.value = sessions.value.map((item) => item.session.id === sessionId
      ? { ...item, ...patch }
      : item);
  }

  /** Moves a session to a project, with the rest of its workflow run: Fleet moves a run's steps together. */
  function patchSessionProject(sessionId: string, projectId: string | null, projectName: string | null): void {
    const runId = sessions.value.find((item) => item.session.id === sessionId)?.workflowRunId ?? null;
    sessions.value = sessions.value.map((item) => item.session.id === sessionId || (runId !== null && item.workflowRunId === runId)
      ? { ...item, projectId, projectName }
      : item);
  }

  function upsertSession(nextSession: SessionListItem): void {
    const existingSession = sessions.value.find((item) => item.session.id === nextSession.session.id);
    if (existingSession) {
      Object.assign(existingSession, nextSession);
      return;
    }

    // The list is newest first, and a session it hasn't seen yet has just been created or forked.
    sessions.value = [nextSession, ...sessions.value];
  }

  function removeSession(sessionId: string): void {
    const wasElsewhere = elsewhere.value.has(sessionId);
    if (wasElsewhere) {
      const next = new Map(elsewhere.value);
      next.delete(sessionId);
      elsewhere.value = next;
    }

    const sessionIndex = sessions.value.findIndex((item) => item.session.id === sessionId);
    if (sessionIndex < 0 && !wasElsewhere) {
      return;
    }

    if (sessionIndex >= 0) {
      sessions.value.splice(sessionIndex, 1);
    }

    if (activeSessionId.value === sessionId) {
      activeSessionId.value = null;
    }

    delete sessionStateOverrides.value[sessionId];
  }

  function patchSessionStateOverride(
    sessionId: string,
    patch: SessionStateOverride,
  ): void {
    sessionStateOverrides.value = {
      ...sessionStateOverrides.value,
      [sessionId]: {
        ...sessionStateOverrides.value[sessionId],
        ...patch,
      },
    };
  }

  function clearSessionStateOverride(sessionId: string): void {
    if (!(sessionId in sessionStateOverrides.value)) {
      return;
    }

    const nextOverrides = { ...sessionStateOverrides.value };
    delete nextOverrides[sessionId];
    sessionStateOverrides.value = nextOverrides;
  }

  return {
    sessions,
    activeSessionId,
    retentionStatus,
    sessionStateOverrides,
    listLoaded,
    elsewhere,
    sessionById,
    upsertElsewhere,
    forgetElsewhere,
    setActiveSessionId,
    setRetentionStatus,
    patchSession,
    patchSessionProject,
    upsertSession,
    patchSessionStateOverride,
    removeSession,
    clearSessionStateOverride,
    setSessions,
  };
});
