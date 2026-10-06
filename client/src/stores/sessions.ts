import { defineStore } from "pinia";
import { ref, shallowRef } from "vue";
import type { SessionListItem } from "@/api/client";

type SessionStateOverride = Partial<Pick<SessionListItem, "activityStatus" | "lifecycleStatus" | "retentionStatus" | "sessionStatus">>;

export const useSessionsStore = defineStore("sessions", () => {
  const sessions = ref<SessionListItem[]>([]);
  const activeSessionId = shallowRef<string | null>(null);
  const retentionStatus = shallowRef<"active" | "archived" | "all">("active");
  const sessionStateOverrides = ref<Record<string, SessionStateOverride>>({});
  /** Whether the list has been loaded from Fleet at least once (sessions upserted one by one don't make a list). */
  const listLoaded = shallowRef(false);

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
    const sessionIndex = sessions.value.findIndex((item) => item.session.id === sessionId);
    if (sessionIndex < 0) {
      return;
    }

    sessions.value.splice(sessionIndex, 1);

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
