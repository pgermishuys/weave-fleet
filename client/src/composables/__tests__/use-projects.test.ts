import { describe, expect, it, beforeEach, vi } from "vitest";
import { shallowRef } from "vue";
import { useProjects } from "@/composables/use-projects";
import type { ProjectResponse } from "@/lib/api-types";
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

function createProject(overrides: Partial<ProjectResponse> = {}): ProjectResponse {
  return {
    id: "project-1",
    name: "Alpha",
    description: null,
    type: "user",
    position: 1,
    sessionCount: 2,
    createdAt: "2026-01-01T00:00:00Z",
    updatedAt: "2026-01-02T00:00:00Z",
    ...overrides,
  };
}

describe("useProjects", () => {
  beforeEach(() => {
    apiFetchMock.mockReset();
  });

  it("fetches projects immediately when enabled", async () => {
    const projects = [createProject(), createProject({ id: "project-2", name: "Beta" })];
    apiFetchMock.mockResolvedValue(createJsonResponse(projects));

    const { result } = await mountComposable(() => useProjects());

    expect(apiFetchMock).toHaveBeenCalledWith("/api/projects");
    expect(result.projects.value).toEqual(projects);
    expect(result.isLoading.value).toBe(false);
    expect(result.isRefreshing.value).toBe(false);
    expect(result.error.value).toBeUndefined();
  });

  it("waits for enabled to become true before fetching", async () => {
    const enabled = shallowRef(false);
    const projects = [createProject()];
    apiFetchMock.mockResolvedValue(createJsonResponse(projects));

    const { result } = await mountComposable(() => useProjects({ enabled }));

    expect(apiFetchMock).not.toHaveBeenCalled();
    expect(result.projects.value).toEqual([]);

    enabled.value = true;
    await flushAll();

    expect(apiFetchMock).toHaveBeenCalledTimes(1);
    expect(result.projects.value).toEqual(projects);
  });

  it("uses refreshing state on refetch after the initial load", async () => {
    apiFetchMock.mockResolvedValueOnce(createJsonResponse([createProject()]));

    const { result } = await mountComposable(() => useProjects());
    const deferred = createDeferred<Response>();
    apiFetchMock.mockReturnValueOnce(deferred.promise);

    const refetchPromise = result.refetch();

    expect(result.isRefreshing.value).toBe(true);
    deferred.resolve(createJsonResponse([createProject({ name: "Updated" })]));

    await refetchPromise;
    await flushAll();

    expect(result.isRefreshing.value).toBe(false);
    expect(result.projects.value[0]?.name).toBe("Updated");
  });

  it("captures HTTP failures as composable errors", async () => {
    apiFetchMock.mockResolvedValue(createJsonResponse({ error: "nope" }, 500));

    const { result } = await mountComposable(() => useProjects());

    expect(result.projects.value).toEqual([]);
    expect(result.error.value).toBe("HTTP 500");
  });
});
