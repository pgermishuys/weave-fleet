import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import type { SessionProgressDetail } from "@/lib/session-progress";

const { apiFetchMock } = vi.hoisted(() => ({ apiFetchMock: vi.fn() }));
vi.mock("@/lib/api-client", () => ({ apiFetch: apiFetchMock }));

import { useSessionProgressStore } from "@/stores/session-progress";

function detail(done: number, updatedAt: string, overrides: Partial<SessionProgressDetail> = {}): SessionProgressDetail {
  return {
    sessionId: "s1",
    kind: "todos",
    done,
    total: 3,
    current: "Next",
    todos: [{ content: "Next", status: "in_progress", priority: "medium" }],
    updatedAt,
    plan: null,
    subagents: [],
    ...overrides,
  };
}

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

describe("useSessionProgressStore", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    apiFetchMock.mockReset();
  });

  it("loads a session's progress once", async () => {
    apiFetchMock.mockResolvedValue(json(detail(1, "2026-09-13T12:00:00Z")));
    const store = useSessionProgressStore();

    await Promise.all([store.ensureLoaded("s1"), store.ensureLoaded("s1")]);
    await store.ensureLoaded("s1");

    expect(apiFetchMock).toHaveBeenCalledTimes(1);
    expect(apiFetchMock).toHaveBeenCalledWith("/api/sessions/s1/progress");
    expect(store.progressFor("s1")?.done).toBe(1);
  });

  it("remembers that a session has no progress", async () => {
    apiFetchMock.mockResolvedValue(new Response(null, { status: 204 }));
    const store = useSessionProgressStore();

    await store.ensureLoaded("s1");
    await store.ensureLoaded("s1");

    expect(apiFetchMock).toHaveBeenCalledTimes(1);
    expect(store.progressFor("s1")).toBeNull();
    expect("s1" in store.bySession).toBe(true);
  });

  it("loads again when forced", async () => {
    apiFetchMock
      .mockResolvedValueOnce(json(detail(1, "2026-09-13T12:00:00Z")))
      .mockResolvedValueOnce(json(detail(2, "2026-09-13T12:05:00Z")));
    const store = useSessionProgressStore();

    await store.ensureLoaded("s1");
    await store.ensureLoaded("s1", { force: true });

    expect(store.progressFor("s1")?.done).toBe(2);
  });

  it("keeps a newer push over an older load", async () => {
    let resolve!: (response: Response) => void;
    apiFetchMock.mockReturnValue(new Promise<Response>((r) => { resolve = r; }));
    const store = useSessionProgressStore();

    const load = store.ensureLoaded("s1");
    store.apply(detail(2, "2026-09-13T12:05:00Z"));
    resolve(json(detail(1, "2026-09-13T12:00:00Z")));
    await load;

    expect(store.progressFor("s1")?.done).toBe(2);
  });

  it("forgets a loaded detail when a row summary shows it's out of date", () => {
    const store = useSessionProgressStore();
    store.apply(detail(1, "2026-09-13T12:00:00Z"));

    store.noteSummary({ sessionId: "s1", kind: "todos", done: 1, total: 3, current: "Next" });
    expect(store.progressFor("s1")?.done).toBe(1);

    store.noteSummary({ sessionId: "s1", kind: "todos", done: 2, total: 3, current: "Later" });
    expect("s1" in store.bySession).toBe(false);
  });

  it("keeps working when the request fails", async () => {
    apiFetchMock.mockRejectedValue(new Error("offline"));
    const store = useSessionProgressStore();

    await store.ensureLoaded("s1");

    expect(store.progressFor("s1")).toBeNull();
    expect("s1" in store.bySession).toBe(false);
  });
});
