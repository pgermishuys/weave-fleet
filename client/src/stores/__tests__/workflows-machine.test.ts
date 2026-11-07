import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";

const { apiFetchMock, onMock } = vi.hoisted(() => ({ apiFetchMock: vi.fn(), onMock: vi.fn() }));
vi.mock("@/lib/api-client", () => ({
  apiFetch: apiFetchMock,
  apiFetchOn: (machine: unknown, path: string, init?: RequestInit) => (machine ? onMock(machine, path, init) : apiFetchMock(path, init)),
}));

import { useWorkflowsStore } from "@/stores/workflows";

const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });

describe("a workflows store of another machine", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    apiFetchMock.mockReset();
    onMock.mockReset();
  });

  it("asks that machine for the library, the runs and every change, never the live one", async () => {
    const falcon = { key: "m-falcon", connection: { id: "m-falcon" }, isLive: false, api: {} } as never;
    onMock.mockImplementation(async (_machine: unknown, path: string) =>
      path.startsWith("/api/workflows/runs") ? json([]) : json({ workflows: [], repository: null, repositoryName: null }));
    apiFetchMock.mockImplementation(async () => json({}));

    const store = useWorkflowsStore(falcon);
    await store.loadLibrary("/work/repo");
    await store.ensureLoaded();
    await store.saveFile({ directory: "/work/repo", workflowId: "repo:x", hash: null, text: "name: x" });

    expect(onMock.mock.calls.every(([machine]) => (machine as { id: string }).id === "m-falcon")).toBe(true);
    expect(onMock.mock.calls.map(([, path]) => path)).toEqual([
      "/api/workflows?directory=%2Fwork%2Frepo",
      "/api/workflows/runs?limit=100",
      "/api/workflows/files",
    ]);
    expect(apiFetchMock).not.toHaveBeenCalled();
  });
});
