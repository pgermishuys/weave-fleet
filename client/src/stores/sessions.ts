import { defineStore } from "pinia";
import { ref, shallowRef } from "vue";
import type { SessionListItem } from "@/lib/api-types";

type SessionStateOverride = Partial<Pick<SessionListItem, "activityStatus" | "lifecycleStatus" | "retentionStatus" | "sessionStatus">>;

export const useSessionsStore = defineStore("sessions", () => {
  const sessions = ref<SessionListItem[]>([]);
  const activeSessionId = shallowRef<string | null>(null);
  const retentionStatus = shallowRef<"active" | "archived" | "all">("active");
  const sessionStateOverrides = ref<Record<string, SessionStateOverride>>({});

  function setActiveSessionId(sessionId: string | null): void {
    activeSessionId.value = sessionId;
  }

  function setRetentionStatus(nextRetentionStatus: "active" | "archived" | "all"): void {
    retentionStatus.value = nextRetentionStatus;
  }

  function setSessions(nextSessions: readonly SessionListItem[]): void {
    sessions.value = [...nextSessions];
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

  function upsertSession(nextSession: SessionListItem): void {
    const existingSession = sessions.value.find((item) => item.session.id === nextSession.session.id);
    if (existingSession) {
      Object.assign(existingSession, nextSession);
      return;
    }

    sessions.value = [...sessions.value, nextSession];
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
    setActiveSessionId,
    setRetentionStatus,
    patchSession,
    upsertSession,
    patchSessionStateOverride,
    removeSession,
    clearSessionStateOverride,
    setSessions,
  };
});
