import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { shallowRef } from "vue";
import type { DomainEvent } from "@/lib/domain-events";
import type { ServerCanvasSnapshot } from "@/lib/server-canvas";
import { useAppRunsStore } from "@/stores/app-runs";
import { serverCanvasTabId, useCanvasesStore } from "@/stores/canvases";
import { flushAll, mountComposable } from "./test-utils";

const { apiFetchMock, subscribeV2Mock, reconnectCallbacks } = vi.hoisted(() => ({
  apiFetchMock: vi.fn(),
  subscribeV2Mock: vi.fn(),
  reconnectCallbacks: [] as Array<() => void>,
}));

vi.mock("@/lib/api-client", () => ({ apiFetch: apiFetchMock }));

vi.mock("@/composables/use-weave-socket", () => ({
  useWeaveSocket: () => ({ subscribeV2: subscribeV2Mock }),
  onReconnect: (callback: () => void) => {
    reconnectCallbacks.push(callback);
    return () => reconnectCallbacks.splice(reconnectCallbacks.indexOf(callback), 1);
  },
}));

let onEvent: ((event: DomainEvent) => void) | null = null;

function diagram(canvasId: string, version: number, labels: string[]): ServerCanvasSnapshot {
  return {
    canvasId,
    kind: "diagram",
    title: `Diagram ${canvasId}`,
    version,
    state: { direction: "TB", nodes: labels.map((label, i) => ({ id: `n${i}`, label })), edges: [] },
  };
}

function updated(sessionId: string, canvas: ServerCanvasSnapshot): DomainEvent {
  return { type: "canvas.updated", payload: { sessionId, ...canvas, state: canvas.state as never, actor: "agent", summary: "" } };
}

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function serverTabs(sessionId: string) {
  return useCanvasesStore().sessionCanvases(sessionId).canvases.filter((canvas) => canvas.server);
}

describe("useServerCanvases", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    apiFetchMock.mockReset();
    subscribeV2Mock.mockReset();
    reconnectCallbacks.length = 0;
    onEvent = null;
    subscribeV2Mock.mockImplementation((_topic: string, _onSnapshot: unknown, callback: (event: DomainEvent) => void) => {
      onEvent = callback;
      return () => {
        onEvent = null;
      };
    });
  });

  it("loads the session's canvases and applies its canvas events", async () => {
    apiFetchMock.mockResolvedValue(jsonResponse([diagram("cv_1", 1, ["A"])]));
    const { useServerCanvases } = await import("@/composables/use-server-canvases");

    const { wrapper } = await mountComposable(() => useServerCanvases(shallowRef("s1")));

    expect(apiFetchMock).toHaveBeenCalledWith("/api/sessions/s1/canvases");
    expect(subscribeV2Mock).toHaveBeenCalledWith("session:s1", expect.any(Function), expect.any(Function));
    expect(serverTabs("s1").map((canvas) => canvas.id)).toEqual([serverCanvasTabId("cv_1")]);

    onEvent?.(updated("s1", diagram("cv_1", 2, ["A", "B"])));
    onEvent?.(updated("other-session", diagram("cv_9", 1, ["X"])));

    expect(serverTabs("s1")[0]?.server?.version).toBe(2);
    expect(serverTabs("other-session")).toHaveLength(0);
    wrapper.unmount();
  });

  it("replays events that arrive while the list loads, so an older list can't undo them", async () => {
    let resolveList!: (response: Response) => void;
    apiFetchMock.mockReturnValue(new Promise<Response>((resolve) => {
      resolveList = resolve;
    }));
    const { useServerCanvases } = await import("@/composables/use-server-canvases");
    const { wrapper } = await mountComposable(() => useServerCanvases(shallowRef("s1")));

    onEvent?.(updated("s1", diagram("cv_1", 2, ["A", "B"])));
    onEvent?.(updated("s1", diagram("cv_new", 1, ["N"])));
    resolveList(jsonResponse([diagram("cv_1", 1, ["A"])]));
    await flushAll();

    expect(serverTabs("s1").map((canvas) => [canvas.id, canvas.server?.version])).toEqual([
      [serverCanvasTabId("cv_1"), 2],
      [serverCanvasTabId("cv_new"), 1],
    ]);
    wrapper.unmount();
  });

  it("loads again on reconnect, since canvas events aren't replayed", async () => {
    apiFetchMock.mockResolvedValueOnce(jsonResponse([diagram("cv_1", 1, ["A"])]));
    const { useServerCanvases } = await import("@/composables/use-server-canvases");
    const { wrapper } = await mountComposable(() => useServerCanvases(shallowRef("s1")));

    apiFetchMock.mockResolvedValueOnce(jsonResponse([]));
    reconnectCallbacks.forEach((callback) => callback());
    await flushAll();

    expect(apiFetchMock).toHaveBeenCalledTimes(2);
    expect(serverTabs("s1")).toHaveLength(0);
    wrapper.unmount();
    expect(reconnectCallbacks).toHaveLength(0);
  });

  it("loads and subscribes again when the session changes", async () => {
    apiFetchMock.mockImplementation((path: string) =>
      Promise.resolve(jsonResponse(path.includes("s2") ? [diagram("cv_2", 1, ["B"])] : [])));
    const sessionId = shallowRef("s1");
    const { useServerCanvases } = await import("@/composables/use-server-canvases");
    const { wrapper } = await mountComposable(() => useServerCanvases(sessionId));

    sessionId.value = "s2";
    await flushAll();

    expect(subscribeV2Mock).toHaveBeenLastCalledWith("session:s2", expect.any(Function), expect.any(Function));
    expect(serverTabs("s2").map((canvas) => canvas.id)).toEqual([serverCanvasTabId("cv_2")]);
    wrapper.unmount();
  });
});

describe("closeServerCanvas", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    apiFetchMock.mockReset();
  });

  it("removes the tab at once and asks the server to close it", async () => {
    const store = useCanvasesStore();
    store.setServerCanvases("s1", [diagram("cv_1", 1, ["A"])]);
    apiFetchMock.mockResolvedValue(new Response(null, { status: 204 }));
    const { closeServerCanvas } = await import("@/composables/use-server-canvases");

    const done = closeServerCanvas("s1", "cv_1");

    expect(serverTabs("s1")).toHaveLength(0);
    await done;
    expect(apiFetchMock).toHaveBeenCalledWith("/api/sessions/s1/canvases/cv_1", { method: "DELETE" });
  });

  it("brings the tab back when the server refuses", async () => {
    const store = useCanvasesStore();
    store.setServerCanvases("s1", [diagram("cv_1", 1, ["A"])]);
    apiFetchMock
      .mockResolvedValueOnce(jsonResponse({ error: "nope" }, 500))
      .mockResolvedValueOnce(jsonResponse([diagram("cv_1", 1, ["A"])]));
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    const { closeServerCanvas } = await import("@/composables/use-server-canvases");

    await closeServerCanvas("s1", "cv_1");

    expect(serverTabs("s1").map((canvas) => canvas.id)).toEqual([serverCanvasTabId("cv_1")]);
    warn.mockRestore();
  });

  it("keeps the session's apps current and pulses the tabs of a page Fleet reloaded", async () => {
    const shop: ServerCanvasSnapshot = { canvasId: "cv_shop", kind: "browser", title: "Shop", version: 1, state: { url: "http://localhost:5173/", appId: "app_1" } };
    apiFetchMock.mockResolvedValue(jsonResponse([shop, diagram("cv_2", 1, ["A"])]));
    const { useServerCanvases } = await import("@/composables/use-server-canvases");
    const { wrapper } = await mountComposable(() => useServerCanvases(shallowRef("s1")));
    const app = { sessionId: "s1", appId: "app_1", command: "npm run dev", url: "http://localhost:5173/", ports: [5173], exitCode: null };

    onEvent?.({ type: "app.updated", payload: { ...app, status: "running", reason: "ready" } });
    onEvent?.({ type: "app.updated", payload: { ...app, sessionId: "s2", appId: "app_9", status: "running", reason: "ready" } });

    expect(useAppRunsStore().byId.app_1?.status).toBe("running");
    expect(useAppRunsStore().byId.app_9).toBeUndefined();
    expect(useCanvasesStore().updatedAt).toEqual({});

    onEvent?.({ type: "app.updated", payload: { ...app, status: "running", reason: "reloaded" } });

    expect(Object.keys(useCanvasesStore().updatedAt)).toEqual([serverCanvasTabId("cv_shop")]);
    wrapper.unmount();
  });

  it("brings a canvas forward here and through Fleet, which reopens a closed one", async () => {
    const store = useCanvasesStore();
    store.setServerCanvases("s1", [diagram("cv_1", 1, ["A"]), diagram("cv_2", 1, ["B"])]);
    apiFetchMock.mockResolvedValue(jsonResponse(diagram("cv_1", 1, ["A"])));
    const { focusServerCanvas } = await import("@/composables/use-server-canvases");

    await focusServerCanvas("s1", "cv_1");

    expect(store.sessionCanvases("s1").activeId).toBe(serverCanvasTabId("cv_1"));
    expect(apiFetchMock).toHaveBeenCalledWith("/api/sessions/s1/canvases/cv_1/focus", { method: "POST" });
  });
});
