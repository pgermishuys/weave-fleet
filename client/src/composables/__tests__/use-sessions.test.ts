import { beforeEach, describe, expect, it, vi } from "vitest";
import { shallowRef } from "vue";
import { useSessions } from "@/composables/use-sessions";
import type { SessionListItem } from "@/lib/api-types";
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

function createSession(id: string, overrides: Partial<SessionListItem> = {}): SessionListItem {
  return {
    instanceId: `instance-${id}`,
    workspaceId: `workspace-${id}`,
    workspaceDirectory: "/tmp/project",
    workspaceDisplayName: null,
    isolationStrategy: "existing",
    sourceDirectory: null,
    branch: null,
    sessionStatus: "active",
    instanceStatus: "running",
    session: {
      id,
      title: `Session ${id}`,
      time: {
        created: 1,
        updated: 2,
      },
    },
    activityStatus: "busy",
    lifecycleStatus: "running",
    retentionStatus: "active",
    archivedAt: null,
    typedInstanceStatus: "running",
    isHidden: false,
    ...overrides,
  };
}

function getSearchParams(path: string): URLSearchParams {
  return new URL(path, "http://localhost").searchParams;
}

describe("useSessions", () => {
  beforeEach(() => {
    apiFetchMock.mockReset();
    Object.defineProperty(document, "visibilityState", {
      configurable: true,
      value: "visible",
    });
  });

  it("fetches sessions with pagination and retention filters", async () => {
    const retentionStatus = shallowRef<"active" | "archived" | "all">("archived");
    const sessions = [createSession("sess-1"), createSession("sess-2")];
    apiFetchMock.mockResolvedValue(createJsonResponse(sessions));

    const { result } = await mountComposable(() => useSessions({
      retentionStatus,
      initialPage: 2,
      initialPageSize: 2,
      pollIntervalMs: 0,
    }));

    const firstCallPath = apiFetchMock.mock.calls[0]?.[0] as string;
    const firstCallParams = getSearchParams(firstCallPath);
    expect(firstCallParams.get("limit")).toBe("2");
    expect(firstCallParams.get("offset")).toBe("2");
    expect(firstCallParams.get("retentionStatus")).toBe("archived");
    expect(result.sessions.value).toEqual(sessions);
    expect(result.hasPreviousPage.value).toBe(true);
    expect(result.hasNextPage.value).toBe(true);

    result.nextPage();
    await flushAll();

    const secondCallPath = apiFetchMock.mock.calls[1]?.[0] as string;
    expect(getSearchParams(secondCallPath).get("offset")).toBe("4");

    result.setPageSize(1);
    await flushAll();

    const thirdCallPath = apiFetchMock.mock.calls[2]?.[0] as string;
    const thirdCallParams = getSearchParams(thirdCallPath);
    expect(thirdCallParams.get("limit")).toBe("1");
    expect(thirdCallParams.get("offset")).toBe("0");
    expect(result.page.value).toBe(1);
  });

  it("refetches while hidden only when explicitly requested", async () => {
    apiFetchMock.mockResolvedValue(createJsonResponse([createSession("sess-1")]));

    const { result } = await mountComposable(() => useSessions({ pollIntervalMs: 0 }));

    expect(apiFetchMock).toHaveBeenCalledTimes(1);

    Object.defineProperty(document, "visibilityState", {
      configurable: true,
      value: "hidden",
    });
    document.dispatchEvent(new Event("visibilitychange"));
    await flushAll();

    expect(apiFetchMock).toHaveBeenCalledTimes(1);

    await result.refetch();
    await flushAll();

    expect(apiFetchMock).toHaveBeenCalledTimes(2);

    Object.defineProperty(document, "visibilityState", {
      configurable: true,
      value: "visible",
    });
    const callCountBeforeVisible = apiFetchMock.mock.calls.length;
    document.dispatchEvent(new Event("visibilitychange"));
    await flushAll();

    expect(apiFetchMock.mock.calls.length).toBeGreaterThan(callCountBeforeVisible);
  });

  it("stores fetch failures as errors", async () => {
    apiFetchMock.mockResolvedValue(createJsonResponse({ error: "boom" }, 503));

    const { result } = await mountComposable(() => useSessions({ pollIntervalMs: 0 }));

    expect(result.error.value).toBe("HTTP 503");
    expect(result.isLoading.value).toBe(false);
    expect(result.isRefreshing.value).toBe(false);
  });
});
