import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import type { AppUpdated } from "@/lib/domain-events";
import { addressForPort, currentRunLines, RESTART_MARKER, useAppRunsStore } from "@/stores/app-runs";

const { apiFetchMock } = vi.hoisted(() => ({ apiFetchMock: vi.fn() }));
vi.mock("@/lib/api-client", () => ({ apiFetch: apiFetchMock }));

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function appUpdated(status: AppUpdated["payload"]["status"], reason: AppUpdated["payload"]["reason"]): AppUpdated {
  return {
    type: "app.updated",
    payload: { sessionId: "s1", appId: "app_1", command: "npm run dev", status, url: status === "running" ? "http://localhost:5173/" : null, ports: [], exitCode: null, reason },
  };
}

const response = { id: "app_1", command: "npm run dev", status: "starting", exitCode: null, url: null, ports: [], logs: [] };

describe("app runs", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    apiFetchMock.mockReset();
  });

  it("takes an app from its events", () => {
    const store = useAppRunsStore();

    store.applyEvent(appUpdated("running", "ready"));

    expect(store.byId.app_1).toMatchObject({ status: "running", url: "http://localhost:5173/", reason: "ready", sessionId: "s1" });
  });

  it("doesn't let a load that was read before an event undo it", async () => {
    const store = useAppRunsStore();
    let answer!: (value: Response) => void;
    apiFetchMock.mockReturnValue(new Promise<Response>((resolve) => { answer = resolve; }));

    const loading = store.load("s1", "app_1");
    store.applyEvent(appUpdated("running", "ready"));
    answer(jsonResponse(response));
    await loading;

    expect(store.byId.app_1?.status).toBe("running");
  });

  it("returns why Fleet refused a start", async () => {
    apiFetchMock.mockResolvedValue(jsonResponse({ error: "This session already runs 3 apps, its limit." }, 409));

    expect(await useAppRunsStore().act("s1", "app_1", "restart")).toBe("This session already runs 3 apps, its limit.");
    expect(apiFetchMock).toHaveBeenCalledWith("/api/sessions/s1/apps/app_1/restart", { method: "POST" });
  });

  it("asks only for output it doesn't have, and starts over when Fleet lost it", async () => {
    const store = useAppRunsStore();
    apiFetchMock
      .mockResolvedValueOnce(jsonResponse({ lines: ["a", "b"], next: 2 }))
      .mockResolvedValueOnce(jsonResponse({ lines: ["c"], next: 3 }))
      .mockResolvedValueOnce(jsonResponse({ lines: [], next: 1 }))
      .mockResolvedValueOnce(jsonResponse({ lines: ["x"], next: 1 }));

    await store.fetchOutput("s1", "app_1");
    await store.fetchOutput("s1", "app_1");
    expect(apiFetchMock).toHaveBeenLastCalledWith("/api/sessions/s1/apps/app_1/output?after=2");
    expect(store.outputById.app_1).toEqual({ lines: ["a", "b", "c"], next: 3 });

    await store.fetchOutput("s1", "app_1");
    expect(apiFetchMock).toHaveBeenLastCalledWith("/api/sessions/s1/apps/app_1/output?after=0");
    expect(store.outputById.app_1).toEqual({ lines: ["x"], next: 1 });
  });

  it("starts a command and returns the canvas that shows it", async () => {
    const store = useAppRunsStore();
    apiFetchMock.mockResolvedValue(jsonResponse({ app: response, canvasId: "cv_1" }));

    expect(await store.startCommand("s1", "npm run dev")).toBe("cv_1");
    expect(apiFetchMock).toHaveBeenCalledWith("/api/sessions/s1/apps", expect.objectContaining({ method: "POST", body: JSON.stringify({ command: "npm run dev" }) }));
    expect(store.byId.app_1?.status).toBe("starting");
  });

  it("keeps only the current run's output for the panel", () => {
    expect(currentRunLines(["old", "crash", RESTART_MARKER, "$ npm run dev", "ready"])).toEqual(["$ npm run dev", "ready"]);
    expect(currentRunLines(["first run"])).toEqual(["first run"]);
  });

  it("keeps the printed addresses it loaded when an event comes, and drops them on a restart", async () => {
    const store = useAppRunsStore();
    apiFetchMock.mockResolvedValueOnce(jsonResponse({ ...response, printedUrls: ["https://localhost:17155/login?t=abc"] }));

    await store.load("s1", "app_1");
    store.applyEvent(appUpdated("running", "ready"));
    expect(store.byId.app_1?.printedUrls).toEqual(["https://localhost:17155/login?t=abc"]);

    store.applyEvent(appUpdated("starting", "restarted"));
    expect(store.byId.app_1?.printedUrls).toEqual([]);
  });

  it("opens a port at the link the app printed for it, a sign-in link first", () => {
    const printed = ["https://localhost:17155/", "https://localhost:17155/login?t=abc", "http://localhost:5173/app/"];

    expect(addressForPort(17155, printed)).toBe("https://localhost:17155/login?t=abc");
    expect(addressForPort(5173, printed)).toBe("http://localhost:5173/app/");
    expect(addressForPort(5199, printed)).toBe("http://localhost:5199/");
    expect(addressForPort(443, ["https://localhost/"])).toBe("https://localhost/");
  });
});
