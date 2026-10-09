import { createPinia, setActivePinia } from "pinia";
import { beforeEach, describe, expect, it } from "vitest";
import { canvasIcon, canvasTitle, CANVAS_TYPES, PICKABLE_CANVAS_KINDS, fileIcon } from "@/lib/canvas-registry";
import { getVisualRenderer } from "@/lib/visual-renderer-registry";
import { useCanvasesStore, type CanvasInstance } from "@/stores/canvases";

describe("canvas kinds", () => {
  it("has these kinds, each with its own label", () => {
    expect(Object.keys(CANVAS_TYPES)).toEqual([
      "changes", "files", "context", "progress", "turns", "agents", "visual", "browser", "page", "file",
    ]);
    expect(Object.values(CANVAS_TYPES).map((type) => type.label)).toEqual([
      "Changes", "Files", "Context", "Progress", "Turns", "Agents", "Diagram", "Browser", "Page", "File",
    ]);
    for (const [kind, type] of Object.entries(CANVAS_TYPES)) {
      expect(type.kind).toBe(kind);
      expect(type.component).toBeTruthy();
      expect(type.icon).toBeTruthy();
    }
  });

  it("offers these in the + menu, in this order", () => {
    expect([...PICKABLE_CANVAS_KINDS]).toEqual(["context", "progress", "changes", "files", "turns", "agents"]);
  });

  it("titles a canvas by its file, page, browser, payload, or kind", () => {
    const base = { id: "x", kind: "changes" } as CanvasInstance;
    expect(canvasTitle(base)).toBe("Changes");
    expect(canvasTitle({ ...base, kind: "file", file: { path: "src/a/b.ts", preview: false, view: "edit" } })).toBe("b.ts");
    expect(canvasTitle({ ...base, kind: "page", page: { title: "Options" } as never })).toBe("Options");
    expect(canvasTitle({ ...base, kind: "browser", browser: { url: "http://x", title: "Tab" } })).toBe("Tab");
    expect(canvasTitle({ ...base, kind: "visual", payload: { $type: "markdown", content: "# Notes", title: "Notes" } })).toBe("Notes");
  });

  it("icons a canvas by its file, payload or kind", () => {
    const base = { id: "x", kind: "files" } as CanvasInstance;
    expect(canvasIcon(base)).toBe(CANVAS_TYPES.files.icon);
    expect(canvasIcon({ ...base, kind: "file", file: { path: "a.json", preview: false, view: "edit" } })).toBe(fileIcon("a.json"));
    expect(canvasIcon({ ...base, kind: "visual", payload: { $type: "html", content: "<p/>" } })).not.toBe(CANVAS_TYPES.visual.icon);
  });

  it("throws for a kind nobody registered", () => {
    expect(() => canvasTitle({ id: "x", kind: "mystery" as never })).toThrow(TypeError);
  });
});

describe("a server canvas of a kind the client does not know", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    localStorage.clear();
  });

  it("is left out of the session's canvases", () => {
    const store = useCanvasesStore();
    store.setServerCanvases("s1", [
      { canvasId: "cv_new", kind: "hologram", title: "From the future", version: 1, state: {} },
      { canvasId: "cv_seq", kind: "sequence", title: "Calls", version: 1, state: { source: "sequenceDiagram" } },
    ]);

    expect(store.sessionCanvases("s1").canvases.map((canvas) => canvas.id)).toEqual(["changes", "files", "canvas:cv_seq"]);
  });
});

describe("visual renderers", () => {
  it("has a different renderer for each payload type", () => {
    const all = ["html", "markdown", "visual/flow", "visual/sequence"].map((type) => getVisualRenderer(type));
    expect(all.every(Boolean)).toBe(true);
    expect(new Set(all).size).toBe(4);
  });

  it("has none for a type nobody handles", () => {
    expect(getVisualRenderer("visual/hologram")).toBeNull();
    expect(getVisualRenderer("")).toBeNull();
  });
});
