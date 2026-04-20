import { computed, readonly, shallowRef, type ComputedRef, type ShallowRef } from "vue";
import { getActivePinia } from "pinia";
import type {
  CreateProjectRequest,
  CreateSessionRequest,
  CreateSessionResponse,
  ForkSessionRequest,
  ForkSessionResponse,
  ProjectResponse,
  ResumeSessionResponse,
  SessionListItem,
  SessionSourceSelection,
  UpdateProjectRequest,
} from "@/lib/api-types";
import { apiFetch } from "@/lib/api-client";
import { dispatchSessionUpsert } from "@/lib/session-sync";
import { useSessionsStore } from "@/stores/sessions";

export interface CreateSessionOptions {
  title?: string;
  isolationStrategy?: "existing" | "worktree" | "clone";
  branch?: string;
  source?: SessionSourceSelection;
  harnessType?: string;
  projectId?: string;
}

export interface UseCreateSessionResult {
  createSession: (directory?: string, opts?: CreateSessionOptions) => Promise<CreateSessionResponse>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

export interface UseCreateProjectResult {
  createProject: (request: CreateProjectRequest) => Promise<ProjectResponse>;
  isCreating: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

export type DeleteProjectMode = "move_to_scratch" | "delete_sessions";

export interface UseDeleteSessionResult {
  deleteSession: (sessionId: string, instanceId: string) => Promise<void>;
  isDeleting: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

export interface UseDeleteProjectResult {
  deleteProject: (projectId: string, mode?: DeleteProjectMode) => Promise<void>;
  isDeleting: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

export interface UseRenameSessionResult {
  renameSession: (sessionId: string, title: string, onSuccess?: () => void) => Promise<void>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

export interface UseMoveSessionResult {
  moveSession: (sessionId: string, projectId: string | null) => Promise<void>;
  isMoving: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

export interface UseArchiveSessionResult {
  archiveSession: (sessionId: string) => Promise<void>;
  isArchiving: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

export interface UseUnarchiveSessionResult {
  unarchiveSession: (sessionId: string) => Promise<void>;
  isUnarchiving: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

export interface UseForkSessionResult {
  forkSession: (sessionId: string, opts?: ForkSessionRequest) => Promise<ForkSessionResponse>;
  clearError: () => void;
  isForking: ComputedRef<boolean>;
  forkingSessionId: Readonly<ShallowRef<string | null>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

export interface UseResumeSessionResult {
  resumeSession: (sessionId: string) => Promise<ResumeSessionResponse>;
  isResuming: ComputedRef<boolean>;
  resumingSessionId: Readonly<ShallowRef<string | null>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

export interface UseAbortSessionResult {
  abortSession: (sessionId: string, instanceId: string) => Promise<void>;
  isAborting: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

export interface TerminateSessionOptions {
  cleanupWorkspace?: boolean;
}

export interface UseTerminateSessionResult {
  terminateSession: (
    sessionId: string,
    instanceId: string,
    opts?: TerminateSessionOptions,
  ) => Promise<void>;
  isTerminating: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

export interface UseUpdateProjectResult {
  updateProject: (projectId: string, request: UpdateProjectRequest) => Promise<ProjectResponse>;
  isUpdating: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

export interface UseReorderProjectResult {
  reorderProject: (projectId: string, newPosition: number) => Promise<void>;
  isReordering: Readonly<ShallowRef<boolean>>;
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

  return {
    isPending,
    error,
    execute,
  };
}

function getSessionsStoreSafely() {
  const pinia = getActivePinia();
  return pinia ? useSessionsStore(pinia) : null;
}

function buildForkedSessionListItem(
  sourceSession: SessionListItem | undefined,
  response: ForkSessionResponse,
): SessionListItem {
  return {
    instanceId: response.instanceId,
    workspaceId: response.workspaceId,
    workspaceDirectory: sourceSession?.workspaceDirectory ?? sourceSession?.sourceDirectory ?? "",
    workspaceDisplayName: sourceSession?.workspaceDisplayName ?? null,
    isolationStrategy: sourceSession?.isolationStrategy ?? "existing",
    sessionStatus: "idle",
    session: response.session,
    instanceStatus: "running",
    parentSessionId: null,
    sourceDirectory: sourceSession?.sourceDirectory ?? null,
    branch: sourceSession?.branch ?? null,
    activityStatus: "idle",
    lifecycleStatus: "running",
    retentionStatus: "active",
    archivedAt: null,
    typedInstanceStatus: "running",
    isHidden: false,
    totalTokens: sourceSession?.totalTokens,
    totalCost: sourceSession?.totalCost,
    projectId: sourceSession?.projectId ?? null,
    projectName: sourceSession?.projectName ?? null,
  };
}

export function useCreateSession(): UseCreateSessionResult {
  const state = createMutationState();

  async function createSession(directory?: string, opts?: CreateSessionOptions): Promise<CreateSessionResponse> {
    return state.execute(async () => {
      const body: CreateSessionRequest = {
        directory,
        title: opts?.title,
        isolationStrategy: opts?.isolationStrategy,
        branch: opts?.branch,
        source: opts?.source,
        harnessType: opts?.harnessType,
        projectId: opts?.projectId,
      };

      const response = await apiFetch("/api/sessions", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      });

      if (!response.ok) {
        throw new Error(await readErrorMessage(response));
      }

      return (await response.json()) as CreateSessionResponse;
    }, "Failed to create session");
  }

  return {
    createSession,
    isLoading: readonly(state.isPending),
    error: readonly(state.error),
  };
}

export function useCreateProject(): UseCreateProjectResult {
  const state = createMutationState();

  async function createProject(request: CreateProjectRequest): Promise<ProjectResponse> {
    return state.execute(async () => {
      const response = await apiFetch("/api/projects", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(request),
      });

      if (!response.ok) {
        throw new Error(await readErrorMessage(response));
      }

      return (await response.json()) as ProjectResponse;
    }, "Failed to create project");
  }

  return {
    createProject,
    isCreating: readonly(state.isPending),
    error: readonly(state.error),
  };
}

export function useDeleteSession(): UseDeleteSessionResult {
  const state = createMutationState();

  async function deleteSession(sessionId: string, instanceId: string): Promise<void> {
    void instanceId;

    await state.execute(async () => {
      const response = await apiFetch(`/api/sessions/${encodeURIComponent(sessionId)}`, {
        method: "DELETE",
      });

      if (!response.ok) {
        throw new Error(await readErrorMessage(response));
      }
    }, "Failed to delete session");
  }

  return {
    deleteSession,
    isDeleting: readonly(state.isPending),
    error: readonly(state.error),
  };
}

export function useDeleteProject(): UseDeleteProjectResult {
  const state = createMutationState();

  async function deleteProject(projectId: string, mode: DeleteProjectMode = "move_to_scratch"): Promise<void> {
    await state.execute(async () => {
      const params = new URLSearchParams({ mode });
      const response = await apiFetch(`/api/projects/${encodeURIComponent(projectId)}?${params.toString()}`, {
        method: "DELETE",
      });

      if (!response.ok) {
        throw new Error(await readErrorMessage(response));
      }
    }, "Failed to delete project");
  }

  return {
    deleteProject,
    isDeleting: readonly(state.isPending),
    error: readonly(state.error),
  };
}

export function useRenameSession(): UseRenameSessionResult {
  const state = createMutationState();
  const sessionsStore = getSessionsStoreSafely();

  async function renameSession(sessionId: string, title: string, onSuccess?: () => void): Promise<void> {
    const existingSession = sessionsStore?.sessions.find((item) => item.session.id === sessionId);
    const previousSession = existingSession
      ? {
          ...existingSession,
          session: {
            ...existingSession.session,
          },
        }
      : null;

    if (existingSession) {
      sessionsStore?.patchSession(sessionId, {
        session: {
          ...existingSession.session,
          title,
        },
      });
    }

    try {
      await state.execute(async () => {
        const response = await apiFetch(`/api/sessions/${encodeURIComponent(sessionId)}`, {
          method: "PATCH",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ title }),
        });

        if (!response.ok) {
          throw new Error(await readErrorMessage(response));
        }

        if (existingSession) {
          dispatchSessionUpsert({
            ...existingSession,
            session: {
              ...existingSession.session,
              title,
            },
          });
        }

        onSuccess?.();
      }, "Failed to rename session");
    } catch (error) {
      if (previousSession) {
        sessionsStore?.patchSession(sessionId, {
          session: previousSession.session,
        });
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

export function useMoveSession(): UseMoveSessionResult {
  const state = createMutationState();

  async function moveSession(sessionId: string, projectId: string | null): Promise<void> {
    await state.execute(async () => {
      const response = await apiFetch(`/api/sessions/${encodeURIComponent(sessionId)}/project`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ projectId }),
      });

      if (!response.ok) {
        throw new Error(await readErrorMessage(response));
      }
    }, "Failed to move session");
  }

  return {
    moveSession,
    isMoving: readonly(state.isPending),
    error: readonly(state.error),
  };
}

function createRetentionMutation(targetStatus: "archived" | "active", fallbackMessage: string) {
  const state = createMutationState();

  async function updateRetention(sessionId: string): Promise<void> {
    await state.execute(async () => {
      const response = await apiFetch(`/api/sessions/${encodeURIComponent(sessionId)}/retention`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ retentionStatus: targetStatus }),
      });

      if (!response.ok) {
        throw new Error(await readErrorMessage(response));
      }
    }, fallbackMessage);
  }

  return {
    updateRetention,
    isPending: readonly(state.isPending),
    error: readonly(state.error),
  };
}

export function useArchiveSession(): UseArchiveSessionResult {
  const mutation = createRetentionMutation("archived", "Failed to archive session");

  return {
    archiveSession: mutation.updateRetention,
    isArchiving: mutation.isPending,
    error: mutation.error,
  };
}

export function useUnarchiveSession(): UseUnarchiveSessionResult {
  const mutation = createRetentionMutation("active", "Failed to unarchive session");

  return {
    unarchiveSession: mutation.updateRetention,
    isUnarchiving: mutation.isPending,
    error: mutation.error,
  };
}

export function useForkSession(): UseForkSessionResult {
  const error = shallowRef<string | undefined>(undefined);
  const forkingSessionId = shallowRef<string | null>(null);
  const isForking = computed(() => forkingSessionId.value !== null);
  const sessionsStore = getSessionsStoreSafely();

  async function forkSession(sessionId: string, opts?: ForkSessionRequest): Promise<ForkSessionResponse> {
    forkingSessionId.value = sessionId;
    error.value = undefined;

    try {
      const sourceSession = sessionsStore?.sessions.find((item) => item.session.id === sessionId);
      const response = await apiFetch(`/api/sessions/${encodeURIComponent(sessionId)}/fork`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(opts ?? {}),
      });

      if (!response.ok) {
        throw new Error(await readErrorMessage(response));
      }

      const payload = (await response.json()) as ForkSessionResponse;
      const nextSession = buildForkedSessionListItem(sourceSession, payload);

      sessionsStore?.upsertSession(nextSession);
      sessionsStore?.setActiveSessionId(payload.session.id);
      dispatchSessionUpsert(nextSession);

      return payload;
    } catch (requestError) {
      const message = requestError instanceof Error ? requestError.message : "Failed to fork session";
      error.value = message;
      throw requestError instanceof Error ? requestError : new Error(message);
    } finally {
      forkingSessionId.value = null;
    }
  }

  return {
    forkSession,
    clearError: () => {
      error.value = undefined;
    },
    isForking,
    forkingSessionId: readonly(forkingSessionId),
    error: readonly(error),
  };
}

export function useResumeSession(): UseResumeSessionResult {
  const error = shallowRef<string | undefined>(undefined);
  const resumingSessionId = shallowRef<string | null>(null);
  const isResuming = computed(() => resumingSessionId.value !== null);

  async function resumeSession(sessionId: string): Promise<ResumeSessionResponse> {
    resumingSessionId.value = sessionId;
    error.value = undefined;

    try {
      const response = await apiFetch(`/api/sessions/${encodeURIComponent(sessionId)}/resume`, {
        method: "POST",
      });

      if (!response.ok) {
        if (response.status === 409) {
          throw new Error("Session is already active");
        }

        throw new Error(await readErrorMessage(response));
      }

      return (await response.json()) as ResumeSessionResponse;
    } catch (requestError) {
      const message = requestError instanceof Error ? requestError.message : "Failed to resume session";
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

export function useAbortSession(): UseAbortSessionResult {
  const state = createMutationState();

  async function abortSession(sessionId: string, instanceId: string): Promise<void> {
    await state.execute(async () => {
      const params = new URLSearchParams({ instanceId });
      const response = await apiFetch(`/api/sessions/${encodeURIComponent(sessionId)}/abort?${params.toString()}`, {
        method: "POST",
      });

      if (!response.ok) {
        throw new Error(await readErrorMessage(response));
      }
    }, "Failed to abort session");
  }

  return {
    abortSession,
    isAborting: readonly(state.isPending),
    error: readonly(state.error),
  };
}

export function useTerminateSession(): UseTerminateSessionResult {
  const state = createMutationState();

  async function terminateSession(
    sessionId: string,
    instanceId: string,
    opts?: TerminateSessionOptions,
  ): Promise<void> {
    void instanceId;
    void opts;

    await state.execute(async () => {
      const response = await apiFetch(`/api/sessions/${encodeURIComponent(sessionId)}/stop`, {
        method: "POST",
      });

      if (!response.ok) {
        throw new Error(await readErrorMessage(response));
      }
    }, "Failed to terminate session");
  }

  return {
    terminateSession,
    isTerminating: readonly(state.isPending),
    error: readonly(state.error),
  };
}

export function useUpdateProject(): UseUpdateProjectResult {
  const state = createMutationState();

  async function updateProject(projectId: string, request: UpdateProjectRequest): Promise<ProjectResponse> {
    return state.execute(async () => {
      const response = await apiFetch(`/api/projects/${encodeURIComponent(projectId)}`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(request),
      });

      if (!response.ok) {
        throw new Error(await readErrorMessage(response));
      }

      return (await response.json()) as ProjectResponse;
    }, "Failed to update project");
  }

  return {
    updateProject,
    isUpdating: readonly(state.isPending),
    error: readonly(state.error),
  };
}

export function useReorderProject(): UseReorderProjectResult {
  const state = createMutationState();

  async function reorderProject(projectId: string, newPosition: number): Promise<void> {
    await state.execute(async () => {
      const response = await apiFetch(`/api/projects/${encodeURIComponent(projectId)}/reorder`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ position: newPosition }),
      });

      if (!response.ok) {
        throw new Error(await readErrorMessage(response));
      }
    }, "Failed to reorder project");
  }

  return {
    reorderProject,
    isReordering: readonly(state.isPending),
    error: readonly(state.error),
  };
}
