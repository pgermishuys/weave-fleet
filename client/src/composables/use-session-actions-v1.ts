/**
 * V1 session action composables — frozen mirror of the V2 equivalents.
 * All mutations hit /api/sessions-v1/* instead of /api/sessions/*.
 * Intentionally minimal: no fork, no move-to-project, no source-preview.
 */
import { computed, readonly, shallowRef, type ComputedRef, type ShallowRef } from "vue";
import { getActivePinia } from "pinia";
import type {
  CreateSessionResponse,
  ResumeSessionResponse,
} from "@/lib/api-types";
import { apiFetch } from "@/lib/api-client";
import { trackAction } from "@/lib/track-action";
import { dispatchSessionV1Upsert } from "@/lib/session-sync-v1";
import { useSessionsV1Store } from "@/stores/sessions-v1";

export interface CreateSessionV1Options {
  title?: string;
  isolationStrategy?: "existing" | "worktree" | "clone";
  branch?: string;
  harnessType?: string;
}

export interface UseCreateSessionV1Result {
  createSession: (directory?: string, opts?: CreateSessionV1Options) => Promise<CreateSessionResponse>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

export interface UseDeleteSessionV1Result {
  deleteSession: (sessionId: string, instanceId: string) => Promise<void>;
  isDeleting: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

export interface UseRenameSessionV1Result {
  renameSession: (sessionId: string, title: string, onSuccess?: () => void) => Promise<void>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

export interface UseArchiveSessionV1Result {
  archiveSession: (sessionId: string) => Promise<void>;
  isArchiving: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

export interface UseUnarchiveSessionV1Result {
  unarchiveSession: (sessionId: string) => Promise<void>;
  isUnarchiving: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

export interface UseResumeSessionV1Result {
  resumeSession: (sessionId: string) => Promise<ResumeSessionResponse>;
  isResuming: ComputedRef<boolean>;
  resumingSessionId: Readonly<ShallowRef<string | null>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

export interface UseAbortSessionV1Result {
  abortSession: (sessionId: string, instanceId: string) => Promise<void>;
  isAborting: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

export interface UseTerminateSessionV1Result {
  terminateSession: (sessionId: string, instanceId: string) => Promise<void>;
  isTerminating: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

async function readErrorMessage(response: Response): Promise<string> {
  const bodyText = await response.text().catch(() => "");
  if (!bodyText) {
    return `HTTP ${response.status}`;
  }

  try {
    const body = JSON.parse(bodyText) as Record<string, unknown>;

    if (typeof body.error === "string" && body.error.trim().length > 0) {
      return body.error;
    }

    if (typeof body.detail === "string" && body.detail.trim().length > 0) {
      return body.detail;
    }

    if (typeof body.title === "string" && body.title.trim().length > 0) {
      return body.title;
    }
  } catch {
    if (bodyText.trim().length > 0) {
      return bodyText.trim();
    }
  }

  return `HTTP ${response.status}`;
}

function createMutationState() {
  const isPending = shallowRef(false);
  const error = shallowRef<string | undefined>(undefined);

  async function execute<T>(request: () => Promise<T>, fallbackMessage: string): Promise<T> {
    isPending.value = true;
    error.value = undefined;

    try {
      return await request();
    } catch (requestError) {
      const message = requestError instanceof Error ? requestError.message : fallbackMessage;
      error.value = message;
      throw requestError instanceof Error ? requestError : new Error(message);
    } finally {
      isPending.value = false;
    }
  }

  return { isPending, error, execute };
}

function getSessionsV1StoreSafely() {
  const pinia = getActivePinia();
  return pinia ? useSessionsV1Store(pinia) : null;
}

export function useCreateSessionV1(): UseCreateSessionV1Result {
  const state = createMutationState();

  async function createSession(directory?: string, opts?: CreateSessionV1Options): Promise<CreateSessionResponse> {
    return state.execute(async () => {
      const body = {
        directory,
        title: opts?.title,
        isolationStrategy: opts?.isolationStrategy,
        branch: opts?.branch,
        harnessType: opts?.harnessType,
      };

      const response = await apiFetch("/api/sessions-v1", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      });

      if (!response.ok) {
        throw new Error(await readErrorMessage(response));
      }

      const result = (await response.json()) as CreateSessionResponse;
      trackAction("session.create", result.session.id);
      return result;
    }, "Failed to create V1 session");
  }

  return {
    createSession,
    isLoading: readonly(state.isPending),
    error: readonly(state.error),
  };
}

export function useDeleteSessionV1(): UseDeleteSessionV1Result {
  const state = createMutationState();

  async function deleteSession(sessionId: string, instanceId: string): Promise<void> {
    void instanceId;

    await state.execute(async () => {
      const response = await apiFetch(`/api/sessions-v1/${encodeURIComponent(sessionId)}`, {
        method: "DELETE",
      });

      if (!response.ok) {
        throw new Error(await readErrorMessage(response));
      }

      trackAction("session.delete", sessionId);
    }, "Failed to delete V1 session");
  }

  return {
    deleteSession,
    isDeleting: readonly(state.isPending),
    error: readonly(state.error),
  };
}

export function useRenameSessionV1(): UseRenameSessionV1Result {
  const state = createMutationState();
  const sessionsStore = getSessionsV1StoreSafely();

  async function renameSession(sessionId: string, title: string, onSuccess?: () => void): Promise<void> {
    const existingSession = sessionsStore?.sessions.find((item) => item.session.id === sessionId);
    const previousSession = existingSession ? { ...existingSession, session: { ...existingSession.session } } : null;

    if (existingSession) {
      sessionsStore?.patchSession(sessionId, { session: { ...existingSession.session, title } });
    }

    try {
      await state.execute(async () => {
        const response = await apiFetch(`/api/sessions-v1/${encodeURIComponent(sessionId)}`, {
          method: "PATCH",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ title }),
        });

        if (!response.ok) {
          throw new Error(await readErrorMessage(response));
        }

        if (existingSession) {
          dispatchSessionV1Upsert({ ...existingSession, session: { ...existingSession.session, title } });
        }

        onSuccess?.();
      }, "Failed to rename V1 session");
    } catch (error) {
      if (previousSession) {
        sessionsStore?.patchSession(sessionId, { session: previousSession.session });
      }

      throw error;
    }
  }

  return {
    renameSession,
    isLoading: readonly(state.isPending),
    error: readonly(state.error),
  };
}

export function useArchiveSessionV1(): UseArchiveSessionV1Result {
  const state = createMutationState();

  async function archiveSession(sessionId: string): Promise<void> {
    await state.execute(async () => {
      const response = await apiFetch(`/api/sessions-v1/${encodeURIComponent(sessionId)}/retention`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ retentionStatus: "archived" }),
      });

      if (!response.ok) {
        throw new Error(await readErrorMessage(response));
      }

      trackAction("session.archive", sessionId);
    }, "Failed to archive V1 session");
  }

  return {
    archiveSession,
    isArchiving: readonly(state.isPending),
    error: readonly(state.error),
  };
}

export function useUnarchiveSessionV1(): UseUnarchiveSessionV1Result {
  const state = createMutationState();

  async function unarchiveSession(sessionId: string): Promise<void> {
    await state.execute(async () => {
      const response = await apiFetch(`/api/sessions-v1/${encodeURIComponent(sessionId)}/retention`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ retentionStatus: "active" }),
      });

      if (!response.ok) {
        throw new Error(await readErrorMessage(response));
      }

      trackAction("session.unarchive", sessionId);
    }, "Failed to unarchive V1 session");
  }

  return {
    unarchiveSession,
    isUnarchiving: readonly(state.isPending),
    error: readonly(state.error),
  };
}

export function useResumeSessionV1(): UseResumeSessionV1Result {
  const error = shallowRef<string | undefined>(undefined);
  const resumingSessionId = shallowRef<string | null>(null);
  const isResuming = computed(() => resumingSessionId.value !== null);

  async function resumeSession(sessionId: string): Promise<ResumeSessionResponse> {
    resumingSessionId.value = sessionId;
    error.value = undefined;

    try {
      const response = await apiFetch(`/api/sessions-v1/${encodeURIComponent(sessionId)}/resume`, {
        method: "POST",
      });

      if (!response.ok) {
        if (response.status === 409) {
          throw new Error("Session is already active");
        }

        throw new Error(await readErrorMessage(response));
      }

      const result = (await response.json()) as ResumeSessionResponse;
      trackAction("session.resume", sessionId);
      return result;
    } catch (requestError) {
      const message = requestError instanceof Error ? requestError.message : "Failed to resume V1 session";
      error.value = message;
      throw requestError instanceof Error ? requestError : new Error(message);
    } finally {
      resumingSessionId.value = null;
    }
  }

  return {
    resumeSession,
    isResuming,
    resumingSessionId: readonly(resumingSessionId),
    error: readonly(error),
  };
}

export function useAbortSessionV1(): UseAbortSessionV1Result {
  const state = createMutationState();

  async function abortSession(sessionId: string, instanceId: string): Promise<void> {
    await state.execute(async () => {
      const params = new URLSearchParams({ instanceId });
      const response = await apiFetch(`/api/sessions-v1/${encodeURIComponent(sessionId)}/abort?${params.toString()}`, {
        method: "POST",
      });

      if (!response.ok) {
        throw new Error(await readErrorMessage(response));
      }

      trackAction("session.abort", sessionId);
    }, "Failed to abort V1 session");
  }

  return {
    abortSession,
    isAborting: readonly(state.isPending),
    error: readonly(state.error),
  };
}

export function useTerminateSessionV1(): UseTerminateSessionV1Result {
  const state = createMutationState();

  async function terminateSession(sessionId: string, instanceId: string): Promise<void> {
    void instanceId;

    await state.execute(async () => {
      const response = await apiFetch(`/api/sessions-v1/${encodeURIComponent(sessionId)}/stop`, {
        method: "POST",
      });

      if (!response.ok) {
        throw new Error(await readErrorMessage(response));
      }
    }, "Failed to stop V1 session");
  }

  return {
    terminateSession,
    isTerminating: readonly(state.isPending),
    error: readonly(state.error),
  };
}
