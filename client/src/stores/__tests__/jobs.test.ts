import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { JOBS_POLL_MS, useJobsStore } from "@/stores/jobs";
import type { Job, JobMachine } from "@/lib/jobs";

const waiting: Job = {
  id: "j1",
  title: "Update the docs",
  ask: "Update the README",
  repository: "https://example.com/acme/docs-site",
  labels: [],
  pinnedMachineId: null,
  state: "waiting",
  machineId: null,
  machineName: null,
  sessionId: null,
  whyWaiting: "No machine has a free seat.",
  error: null,
  createdAt: "2026-10-27T08:00:00Z",
};
const atlas: JobMachine = { id: "m1", name: "atlas", isLocal: true, seats: 2, used: 0, labels: [], status: "online" };

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("jobs store", () => {
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    setActivePinia(createPinia());
    vi.useFakeTimers();
    fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.endsWith("/api/jobs") && !init?.method) return json({ jobs: [waiting], machines: [atlas] });
      if (url.endsWith("/api/jobs") && init?.method === "POST") return json({ ...waiting, id: "j2" });
      return json({ error: "Jobs are turned off in Fleet's Settings." }, 404);
    });
    vi.stubGlobal("fetch", fetchMock);
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("asks nothing until something loads it", () => {
    useJobsStore();

    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("loads the jobs and the machines' seats from the home Fleet", async () => {
    const store = useJobsStore();

    await store.load();

    expect(store.jobs).toEqual([waiting]);
    expect(store.machines).toEqual([atlas]);
    expect(store.isLoaded).toBe(true);
    expect(String(fetchMock.mock.calls[0][0])).toBe("/api/jobs");
  });

  it("says what Fleet said when it refuses", async () => {
    fetchMock.mockResolvedValueOnce(json({ error: "Jobs are turned off in Fleet's Settings." }, 404));
    const store = useJobsStore();

    await store.load();

    expect(store.error).toBe("Jobs are turned off in Fleet's Settings.");
    expect(store.isLoaded).toBe(false);
  });

  it("asks again every few seconds while watched, and stops with the last watcher", async () => {
    const store = useJobsStore();
    const stopFirst = store.watch();
    const stopSecond = store.watch();
    await vi.advanceTimersByTimeAsync(JOBS_POLL_MS * 2);
    const whileWatched = fetchMock.mock.calls.length;

    stopFirst();
    await vi.advanceTimersByTimeAsync(JOBS_POLL_MS);
    expect(fetchMock.mock.calls.length).toBeGreaterThan(whileWatched);

    stopSecond();
    const afterStop = fetchMock.mock.calls.length;
    await vi.advanceTimersByTimeAsync(JOBS_POLL_MS * 3);
    expect(fetchMock.mock.calls.length).toBe(afterStop);
  });

  it("posts a job and loads the queue again", async () => {
    const store = useJobsStore();

    await store.post({ title: "Update the docs", ask: "Update the README", repository: waiting.repository, labels: ["mac"], pinnedMachineId: null });

    const post = fetchMock.mock.calls.find(([, init]) => (init as RequestInit | undefined)?.method === "POST")!;
    expect(JSON.parse(String((post[1] as RequestInit).body))).toEqual({
      title: "Update the docs",
      ask: "Update the README",
      repository: waiting.repository,
      labels: ["mac"],
      pinnedMachineId: null,
    });
    expect(store.jobs).toEqual([waiting]);
  });

  it("keeps a machine's saved seats and labels", async () => {
    const store = useJobsStore();
    await store.load();
    fetchMock.mockResolvedValueOnce(json({ ...atlas, seats: 3, labels: ["mac"] }));

    await store.saveMachine("m1", { seats: 3, labels: ["mac"] });

    expect(store.machines[0]).toMatchObject({ seats: 3, labels: ["mac"] });
    const put = fetchMock.mock.calls.at(-1)!;
    expect(String(put[0])).toBe("/api/jobs/machines/m1");
    expect((put[1] as RequestInit).method).toBe("PUT");
  });
});
