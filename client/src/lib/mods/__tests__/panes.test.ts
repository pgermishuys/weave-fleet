import { createPinia, setActivePinia } from "pinia";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { canvasTitle, canvasTypes } from "@/lib/canvas-registry";
import { closeModPane, openModPane, updateModPane } from "@/lib/mods/panes";
import { modPanes, modPaneViewId, type ModPaneView } from "@/lib/mods/points";
import { installModsTestApi } from "@/lib/mods/test-api";
import { useCanvasesStore } from "@/stores/canvases";

const view = (title = "Test runs", mod = "ci-mod"): ModPaneView => ({
  sessionId: "s1",
  paneId: "runs",
  title,
  mod,
  tree: null,
  mods: [{ name: "ci-mod", draft: false }] as never,
});

const modCanvases = () => useCanvasesStore().sessionCanvases("s1").canvases.filter((canvas) => canvas.kind === "mod");

describe("mod panes", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    modPanes.clear();
  });
  afterEach(() => {
    modPanes.clear();
    vi.unstubAllEnvs();
    delete window.__FLEET_MODS_TEST_API;
  });

  it("registers the mod canvas kind after the core ones", () => {
    const mod = canvasTypes.get("mod");
    expect(mod?.label).toBe("Mod");
    expect(mod?.order).toBeGreaterThan(canvasTypes.get("file")!.order!);
  });

  it("opens one canvas, activates it, and titles the tab from the pane", () => {
    openModPane(view());
    const store = useCanvasesStore();
    expect(modCanvases()).toHaveLength(1);
    expect(store.sessionCanvases("s1").activeId).toBe(modCanvases()[0].id);
    expect(canvasTitle(modCanvases()[0])).toBe("Test runs");
    expect(modPanes.get(modPaneViewId("s1", "ci-mod", "runs"))?.title).toBe("Test runs");
  });

  it("re-opening replaces the view, brings the tab forward and adds no tab", () => {
    openModPane(view());
    const store = useCanvasesStore();
    store.activate("s1", "files");
    openModPane(view("Renamed"));
    expect(modCanvases()).toHaveLength(1);
    expect(store.sessionCanvases("s1").activeId).toBe(modCanvases()[0].id);
    expect(canvasTitle(modCanvases()[0])).toBe("Renamed");
    expect(modPanes.get(modPaneViewId("s1", "ci-mod", "runs"))?.title).toBe("Renamed");
    expect(modPanes.items.value).toHaveLength(1);
  });

  it("updating redraws an open pane and its tab title without pulling it forward or adding a tab", () => {
    openModPane(view());
    const store = useCanvasesStore();
    store.activate("s1", "files");
    updateModPane(view("Renamed"));
    expect(modCanvases()).toHaveLength(1);
    expect(store.sessionCanvases("s1").activeId).toBe("files");
    expect(canvasTitle(modCanvases()[0])).toBe("Renamed");
    expect(modPanes.get(modPaneViewId("s1", "ci-mod", "runs"))?.title).toBe("Renamed");
  });

  it("updating a pane that is not open as a tab keeps its view but opens nothing", () => {
    updateModPane(view());
    expect(modCanvases()).toHaveLength(0);
    expect(modPanes.get(modPaneViewId("s1", "ci-mod", "runs"))).toBeDefined();
  });

  it("two mods with the same pane id get two tabs and two views", () => {
    openModPane(view("One", "mod-one"));
    openModPane(view("Two", "mod-two"));
    expect(modCanvases()).toHaveLength(2);
    expect(modCanvases()[0].id).not.toBe(modCanvases()[1].id);
    expect(modPanes.items.value.map((pane) => pane.title)).toEqual(["One", "Two"]);
    closeModPane("s1", "mod-one", "runs");
    expect(modCanvases().map((canvas) => canvas.modPane?.mod)).toEqual(["mod-two"]);
    expect(modPanes.items.value.map((pane) => pane.title)).toEqual(["Two"]);
  });

  it("closing the tab from the canvas store drops the pane's view (the phone menu stops listing it)", () => {
    openModPane(view());
    const store = useCanvasesStore();
    store.close("s1", modCanvases()[0].id);
    expect(modCanvases()).toHaveLength(0);
    expect(modPanes.get(modPaneViewId("s1", "ci-mod", "runs"))).toBeUndefined();
    expect(modPanes.items.value).toHaveLength(0);
  });

  it("closing removes the view and the canvas", () => {
    openModPane(view());
    closeModPane("s1", "ci-mod", "runs");
    expect(modCanvases()).toHaveLength(0);
    expect(modPanes.get(modPaneViewId("s1", "ci-mod", "runs"))).toBeUndefined();
  });
});

describe("mods test API", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });
  afterEach(() => {
    vi.unstubAllEnvs();
    delete window.__FLEET_MODS_TEST_API;
  });

  it("is installed in dev and in mock mode, and works", () => {
    installModsTestApi();
    const api = window.__FLEET_MODS_TEST_API!;
    api.openModPane(view());
    expect(api.modPanes.get("s1:ci-mod:runs")).toBeDefined();
    api.clear();
    expect(api.modPanes.get("s1:ci-mod:runs")).toBeUndefined();
  });

  it("has an update-only path that does not activate the tab", () => {
    installModsTestApi();
    const api = window.__FLEET_MODS_TEST_API!;
    api.openModPane(view());
    const store = useCanvasesStore();
    store.activate("s1", "files");
    api.updateModPane(view("Again"));
    expect(store.sessionCanvases("s1").activeId).toBe("files");
    expect(api.modPanes.get("s1:ci-mod:runs")?.title).toBe("Again");
  });

  it("is absent from a production build", () => {
    vi.stubEnv("DEV", false);
    vi.stubEnv("MODE", "production");
    installModsTestApi();
    expect(window.__FLEET_MODS_TEST_API).toBeUndefined();
  });

  it("is present in mock mode even when not DEV", () => {
    vi.stubEnv("DEV", false);
    vi.stubEnv("MODE", "mock");
    installModsTestApi();
    expect(window.__FLEET_MODS_TEST_API).toBeDefined();
  });
});
