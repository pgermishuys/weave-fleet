import { beforeEach, describe, expect, it } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { toDomainEvent } from "@/composables/use-signalr-socket";
import type { CanvasClosed, CanvasEvent, CanvasFocused, CanvasUpdated } from "@/lib/domain-events";
import type { VisualPayload } from "@/lib/visual-payload";
import { serverCanvasTabId, useCanvasesStore, visualCanvasId, visualCanvasTitle } from "@/stores/canvases";

const flow: VisualPayload = {
  $type: "visual/flow",
  title: "Session event flow",
  content: { nodes: [], edges: [] },
};

describe("useCanvasesStore", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    localStorage.clear();
  });

  it("starts every session with Changes and Files, Changes active", () => {
    const store = useCanvasesStore();
    const state = store.sessionCanvases("s1");

    expect(state.canvases.map((canvas) => canvas.id)).toEqual(["changes", "files"]);
    expect(state.activeId).toBe("changes");
  });

  it("keeps canvases separate per session", () => {
    const store = useCanvasesStore();

    store.activate("s1", "files");
    store.openVisual("s2", flow);

    expect(store.sessionCanvases("s1").activeId).toBe("files");
    expect(store.sessionCanvases("s1").canvases).toHaveLength(2);
    expect(store.sessionCanvases("s2").activeId).toBe(visualCanvasId(flow));
  });

  it("opens a visual as a new active tab and updates it in place when revised", () => {
    const store = useCanvasesStore();

    store.openVisual("s1", flow);
    store.activate("s1", "changes");
    const revised: VisualPayload = { ...flow, content: { nodes: [{ id: "n1", label: "Hub" }], edges: [] } };
    store.openVisual("s1", revised);

    const state = store.sessionCanvases("s1");
    const visuals = state.canvases.filter((canvas) => canvas.kind === "visual");
    expect(visuals).toHaveLength(1);
    expect(visuals[0]?.payload).toBe(revised);
    expect(state.activeId).toBe(visualCanvasId(flow));
  });

  it("can open a visual without taking focus", () => {
    const store = useCanvasesStore();

    store.openVisual("s1", flow, { activate: false });

    expect(store.sessionCanvases("s1").activeId).toBe("changes");
    expect(store.sessionCanvases("s1").canvases).toHaveLength(3);
  });

  it("closing the active canvas activates its left neighbour", () => {
    const store = useCanvasesStore();

    store.openVisual("s1", flow);
    store.close("s1", visualCanvasId(flow));

    expect(store.sessionCanvases("s1").activeId).toBe("files");
  });

  it("never closes Changes", () => {
    const store = useCanvasesStore();

    store.close("s1", "changes");

    expect(store.sessionCanvases("s1").canvases.map((canvas) => canvas.id)).toContain("changes");
  });

  it("reopens a closed built-in canvas at the end and focuses it", () => {
    const store = useCanvasesStore();

    store.close("s1", "files");
    store.openVisual("s1", flow);
    store.open("s1", "files");

    const state = store.sessionCanvases("s1");
    expect(state.canvases.map((canvas) => canvas.id)).toEqual(["changes", visualCanvasId(flow), "files"]);
    expect(state.activeId).toBe("files");
  });

  it("introduces Context as the first tab once, without taking focus", () => {
    const store = useCanvasesStore();

    store.introduce("s1", "context");
    store.introduce("s1", "context");

    let state = store.sessionCanvases("s1");
    expect(state.canvases.map((canvas) => canvas.id)).toEqual(["context", "changes", "files"]);
    expect(state.activeId).toBe("changes");

    // Closing it keeps it closed; opening it again brings it back.
    store.close("s1", "context");
    store.introduce("s1", "context");
    state = store.sessionCanvases("s1");
    expect(state.canvases.map((canvas) => canvas.id)).toEqual(["changes", "files"]);

    store.open("s1", "context");
    expect(store.sessionCanvases("s1").activeId).toBe("context");
  });

  it("persists Widen across reloads", () => {
    useCanvasesStore().toggleWidened();

    setActivePinia(createPinia());

    expect(useCanvasesStore().widened).toBe(true);
  });

  it("titles visuals by title, then file name, then a generic label", () => {
    expect(visualCanvasTitle(flow)).toBe("Session event flow");
    expect(visualCanvasTitle({ $type: "markdown", content: "", sourceFilePath: "docs/plan.md" })).toBe("plan.md");
    expect(visualCanvasTitle({ $type: "visual/sequence", content: "sequenceDiagram" })).toBe("Diagram");
  });
});

// The canvas events exactly as SignalREventContractTests receives them from the
// hub (open, focus, then a user move), mapped the way the socket maps them.
const SESSION = "ses_1";
const CANVAS = "cv_01";

function wire<T extends CanvasEvent>(json: string): T {
  return toDomainEvent(null, JSON.parse(json)) as T;
}

const opened = wire<CanvasUpdated>(`{"type":"canvas.updated","eventId":null,"properties":{"sessionId":"${SESSION}","canvasId":"${CANVAS}","kind":"diagram","title":"Session event flow","version":1,"actor":"agent","state":{"direction":"TB","nodes":[{"id":"n1","label":"NuCode session","detail":"NuCode/Sessions"},{"id":"n2","label":"SessionEventsHub"}],"edges":[{"id":"e1","from":"n1","to":"n2","label":"publishes","style":"solid"}]},"summary":"+2 boxes, +1 edge"}}`);
const focused = wire<CanvasFocused>(`{"type":"canvas.focused","eventId":null,"properties":{"sessionId":"${SESSION}","canvasId":"${CANVAS}"}}`);
const moved = wire<CanvasUpdated>(`{"type":"canvas.updated","eventId":null,"properties":{"sessionId":"${SESSION}","canvasId":"${CANVAS}","kind":"diagram","title":"Session event flow","version":2,"actor":"user","state":{"direction":"TB","nodes":[{"id":"n1","label":"NuCode session","detail":"NuCode/Sessions","x":20,"y":44.5,"placedByUser":true},{"id":"n2","label":"SessionEventsHub"}],"edges":[{"id":"e1","from":"n1","to":"n2","label":"publishes","style":"solid"}]},"summary":"1 box moved"}}`);
const closed = wire<CanvasClosed>(`{"type":"canvas.closed","eventId":null,"properties":{"sessionId":"${SESSION}","canvasId":"${CANVAS}"}}`);

const TAB = serverCanvasTabId(CANVAS);

function serverTab(store: ReturnType<typeof useCanvasesStore>) {
  return store.sessionCanvases(SESSION).canvases.find((canvas) => canvas.id === TAB);
}

describe("useCanvasesStore server canvases", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    localStorage.clear();
  });

  it("adds a tab for canvas.updated and brings it forward on canvas.focused", () => {
    const store = useCanvasesStore();

    store.applyCanvasEvent(opened);

    expect(store.sessionCanvases(SESSION).canvases.map((canvas) => canvas.id)).toEqual(["changes", "files", TAB]);
    expect(store.sessionCanvases(SESSION).activeId).toBe("changes");
    expect(serverTab(store)).toMatchObject({
      kind: "visual",
      server: { canvasId: CANVAS, kind: "diagram", version: 1 },
      payload: { $type: "visual/flow", title: "Session event flow" },
    });

    store.applyCanvasEvent(focused);

    expect(store.sessionCanvases(SESSION).activeId).toBe(TAB);
  });

  it("redraws the same tab in place on a later version, positions included", () => {
    const store = useCanvasesStore();
    store.applyCanvasEvent(opened);
    store.activate(SESSION, "files");

    store.applyCanvasEvent(moved);

    const tabs = store.sessionCanvases(SESSION).canvases.filter((canvas) => canvas.server);
    expect(tabs).toHaveLength(1);
    expect(tabs[0]?.server?.version).toBe(2);
    expect((tabs[0]?.payload?.content as { nodes: unknown[] }).nodes[0]).toEqual({
      id: "n1", label: "NuCode session", detail: "NuCode/Sessions", x: 20, y: 44.5,
    });
    expect(store.sessionCanvases(SESSION).activeId).toBe("files");
  });

  it("ignores an update older than the tab it would replace", () => {
    const store = useCanvasesStore();
    store.applyCanvasEvent(moved);

    store.applyCanvasEvent(opened);

    expect(serverTab(store)?.server?.version).toBe(2);
  });

  it("removes the tab on canvas.closed, and a second close is a no-op", () => {
    const store = useCanvasesStore();
    store.applyCanvasEvent(opened);
    store.applyCanvasEvent(focused);

    store.applyCanvasEvent(closed);
    store.applyCanvasEvent(closed);

    expect(serverTab(store)).toBeUndefined();
    expect(store.sessionCanvases(SESSION).activeId).toBe("files");
  });

  it("ignores focus for a canvas that isn't open", () => {
    const store = useCanvasesStore();

    store.applyCanvasEvent(focused);

    expect(store.sessionCanvases(SESSION).activeId).toBe("changes");
  });

  it("skips a canvas kind it can't show", () => {
    const store = useCanvasesStore();
    const terminal = wire<CanvasUpdated>(`{"type":"canvas.updated","eventId":null,"properties":{"sessionId":"${SESSION}","canvasId":"cv_t","kind":"terminal","title":"Shell","version":1,"actor":"agent","state":{},"summary":""}}`);

    store.applyCanvasEvent(terminal);

    expect(store.sessionCanvases(SESSION).canvases).toHaveLength(2);
  });

  it("replaces only the server canvases when a list loads", () => {
    const store = useCanvasesStore();
    store.openVisual(SESSION, flow);
    store.applyCanvasEvent(opened);
    store.applyCanvasEvent({ ...opened, payload: { ...opened.payload, canvasId: "cv_gone", title: "Gone" } });
    store.activate(SESSION, serverCanvasTabId("cv_gone"));

    store.setServerCanvases(SESSION, [
      { canvasId: CANVAS, kind: "diagram", title: "Session event flow", version: 2, state: moved.payload.state },
      { canvasId: "cv_seq", kind: "sequence", title: "Calls", version: 1, state: { source: "sequenceDiagram" } },
    ]);

    const state = store.sessionCanvases(SESSION);
    expect(state.canvases.map((canvas) => canvas.id)).toEqual([
      "changes", "files", visualCanvasId(flow), TAB, serverCanvasTabId("cv_seq"),
    ]);
    expect(serverTab(store)?.server?.version).toBe(2);
    expect(state.activeId).toBe("changes");
  });
});
