import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import {
  useRenameSession,
  useCreateSession,
  useDeleteProject,
  useForkSession,
  useResumeSession,
} from "@/composables/use-session-actions";
import type { CreateSessionResponse, ForkSessionResponse, SessionListItem } from "@/lib/api-types";
import { useSessionsStore } from "@/stores/sessions";
import { flushAll, mountComposable } from "./test-utils";

const { apiFetchMock } = vi.hoisted(() => ({
  apiFetchMock: vi.fn(),
}));

vi.mock("@/lib/api-client", () => ({
  apiFetch: apiFetchMock,
}));

function createJsonResponse<T>(body: T, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function createDeferred<T>() {
  let resolve!: (value: T | PromiseLike<T>) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((promiseResolve, promiseReject) => {
    resolve = promiseResolve;
    reject = promiseReject;
  });

  return { promise, resolve, reject };
}

function createSessionListItem(overrides: Partial<SessionListItem> = {}): SessionListItem {
  return {
    instanceId: "instance-1",
    workspaceId: "workspace-1",
    workspaceDirectory: "/tmp/project",
    workspaceDisplayName: "project",
    isolationStrategy: "existing",
    sessionStatus: "active",
    session: {
      id: "session-1",
      title: "Migration",
      time: {
        created: 1,
        updated: 2,
      },
    },
    instanceStatus: "running",
    parentSessionId: null,
    sourceDirectory: "/tmp/project",
    branch: "main",
    activityStatus: "busy",
    lifecycleStatus: "running",
    retentionStatus: "active",
    archivedAt: null,
    typedInstanceStatus: "running",
    isHidden: false,
    projectId: "project-1",
    projectName: "Api",
    totalTokens: 123,
    totalCost: 4.56,
    ...overrides,
  };
}

describe("useSessionActions", () => {
  beforeEach(() => {
    apiFetchMock.mockReset();
    setActivePinia(createPinia());
  });

  it("creates a session and exposes loading state", async () => {
    const responseBody: CreateSessionResponse = {
      instanceId: "instance-1",
      workspaceId: "workspace-1",
      session: {
        id: "session-1",
        title: "My session",
        time: { created: 1, updated: 2 },
      },
    };
    const deferred = createDeferred<Response>();
    apiFetchMock.mockReturnValue(deferred.promise);

    const { result } = await mountComposable(() => useCreateSession());
    const createPromise = result.createSession("/tmp/project", {
      title: "My session",
      isolationStrategy: "clone",
      branch: "feature/tests",
      harnessType: "opencode",
      projectId: "project-1",
    });

    expect(result.isLoading.value).toBe(true);
    expect(apiFetchMock).toHaveBeenCalledWith("/api/sessions", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        directory: "/tmp/project",
        title: "My session",
        isolationStrategy: "clone",
        branch: "feature/tests",
        source: undefined,
        harnessType: "opencode",
        projectId: "project-1",
      }),
    });

    deferred.resolve(createJsonResponse(responseBody));

    await expect(createPromise).resolves.toEqual(responseBody);
    expect(result.isLoading.value).toBe(false);
    expect(result.error.value).toBeUndefined();
  });

  it("reports API errors for project deletion", async () => {
    apiFetchMock.mockResolvedValue(createJsonResponse({ error: "Cannot delete project" }, 400));

    const { result } = await mountComposable(() => useDeleteProject());

    await expect(result.deleteProject("project-1")).rejects.toThrow("Cannot delete project");
    expect(apiFetchMock).toHaveBeenCalledWith("/api/projects/project-1?mode=move_to_scratch", {
      method: "DELETE",
    });
    expect(result.error.value).toBe("Cannot delete project");
    expect(result.isDeleting.value).toBe(false);
  });

  it("tracks the active fork while the request is pending", async () => {
    const sessionsStore = useSessionsStore();
    sessionsStore.setSessions([createSessionListItem()]);

    const responseBody: ForkSessionResponse = {
      instanceId: "instance-2",
      workspaceId: "workspace-2",
      forkedFromSessionId: "session-1",
      session: {
        id: "session-2",
        title: "Forked",
        time: { created: 10, updated: 11 },
      },
    };
    const deferred = createDeferred<Response>();
    apiFetchMock.mockReturnValue(deferred.promise);

    const { result } = await mountComposable(() => useForkSession());
    const forkPromise = result.forkSession("session-1", { title: "Forked" });

    expect(result.isForking.value).toBe(true);
    expect(result.forkingSessionId.value).toBe("session-1");

    deferred.resolve(createJsonResponse(responseBody));

    await expect(forkPromise).resolves.toEqual(responseBody);
    expect(result.isForking.value).toBe(false);
    expect(result.forkingSessionId.value).toBeNull();
    expect(sessionsStore.activeSessionId).toBe("session-2");
    expect(sessionsStore.sessions).toHaveLength(2);
    expect(sessionsStore.sessions[1]).toMatchObject({
      instanceId: "instance-2",
      workspaceId: "workspace-2",
      session: {
        id: "session-2",
        title: "Forked",
      },
      projectId: "project-1",
      projectName: "Api",
      branch: "main",
      sourceDirectory: "/tmp/project",
      lifecycleStatus: "running",
      activityStatus: "idle",
      sessionStatus: "idle",
    });
    expect(apiFetchMock).toHaveBeenCalledWith("/api/sessions/session-1/fork", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ title: "Forked" }),
    });
  });

  it("optimistically renames and rolls back on failure", async () => {
    const sessionsStore = useSessionsStore();
    sessionsStore.setSessions([createSessionListItem()]);

    const deferred = createDeferred<Response>();
    apiFetchMock.mockReturnValue(deferred.promise);

    const { result } = await mountComposable(() => useRenameSession());
    const renamePromise = result.renameSession("session-1", "Renamed session");

    expect(sessionsStore.sessions[0]?.session.title).toBe("Renamed session");

    deferred.reject(new Error("rename failed"));

    await expect(renamePromise).rejects.toThrow("rename failed");
    await flushAll();
    expect(sessionsStore.sessions[0]?.session.title).toBe("Migration");
  });

  it("turns resume conflicts into user-friendly errors", async () => {
    const deferred = createDeferred<Response>();
    apiFetchMock.mockReturnValue(deferred.promise);

    const { result } = await mountComposable(() => useResumeSession());
    const resumePromise = result.resumeSession("session-1");

    expect(result.isResuming.value).toBe(true);
    expect(result.resumingSessionId.value).toBe("session-1");

    deferred.resolve(createJsonResponse({ error: "conflict" }, 409));

    await expect(resumePromise).rejects.toThrow("Session is already active");
    expect(result.error.value).toBe("Session is already active");
    expect(result.isResuming.value).toBe(false);
    expect(result.resumingSessionId.value).toBeNull();
  });
});
