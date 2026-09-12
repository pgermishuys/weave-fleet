import { beforeEach, describe, expect, it } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import type { VisualPayload } from "@/lib/visual-payload";
import { useCanvasesStore, visualCanvasId, visualCanvasTitle } from "@/stores/canvases";

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
