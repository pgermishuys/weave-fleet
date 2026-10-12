import { createPinia, setActivePinia } from "pinia";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { useSessionModPanes } from "@/composables/use-session-mod-panes";
import { modPanes, modPaneViewId } from "@/lib/mods/points";
import type { ModWireElement } from "@/lib/mods/types";
import { modPaneCanvasId, useCanvasesStore } from "@/stores/canvases";
import { useSidebarStore } from "@/stores/sidebar";
import { flushAll, mountComposable } from "./test-utils";

const { fetchMock, listeners } = vi.hoisted(() => ({
  fetchMock: vi.fn(),
  listeners: new Map<string, Set<(event: unknown) => void>>(),
}));
vi.mock("@/lib/api-client", async (importOriginal) => ({ ...(await importOriginal<object>()), apiFetchOn: fetchMock }));
vi.mock("@/composables/use-signalr-socket", async (importOriginal) => ({
  ...(await importOriginal<object>()),
  onGlobalEvent: (_machine: unknown, topic: string, handler: (event: unknown) => void) => {
    if (!listeners.has(topic)) listeners.set(topic, new Set());
    listeners.get(topic)!.add(handler);
    return () => listeners.get(topic)?.delete(handler);
  },
}));

const emit = (topic: string, type: string, payload: unknown) => listeners.get(topic)?.forEach((handler) => handler({ type, payload }));
const answer = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });
const text = (value: string) => ({ type: "Text", props: {}, children: [value] }) as unknown as ModWireElement;
const board = { mod: "release-board", id: "board", title: "Release board" };
const open = (payload = board) => emit("session:s1", "mod.pane", { sessionId: "s1", ...payload, open: true });

const tabs = () => useCanvasesStore().sessionCanvases("s1").canvases.filter((canvas) => canvas.kind === "mod");
const treeOf = () => modPanes.get(modPaneViewId("s1", "release-board", "board"))?.tree ?? null;
const renders = () => fetchMock.mock.calls.map(([, , init]) => JSON.parse((init as RequestInit).body as string));

let drawn: Array<unknown>;

beforeEach(async () => {
  setActivePinia(createPinia());
  modPanes.clear();
  listeners.clear();
  drawn = [{ tree: text("Release 2.4"), mods: [{ name: "release-board", draft: false }] }];
  fetchMock.mockReset().mockImplementation(async () => answer(drawn.length > 1 ? drawn.shift() : drawn[0]));
  await mountComposable(() => useSessionModPanes(() => "s1"));
});

describe("useSessionModPanes", () => {
  it("opens a tab for the pane a mod opened, brings it forward, and asks that mod to draw it", async () => {
    open();
    await flushAll();

    expect(tabs().map((tab) => tab.id)).toEqual([modPaneCanvasId("release-board", "board")]);
    expect(useCanvasesStore().sessionCanvases("s1").activeId).toBe(modPaneCanvasId("release-board", "board"));
    expect(renders()).toEqual([{ site: "Pane", requestId: "board", mod: "release-board", props: { title: "Release board" } }]);
    expect(treeOf()).toEqual(text("Release 2.4"));
  });

  it("opens the content panel when it was closed", async () => {
    const sidebar = useSidebarStore();
    sidebar.setRightPanelCollapsed(true);
    open();
    await flushAll();

    expect(sidebar.rightPanelCollapsed).toBe(false);
  });

  it("closes the tab when the mod closes the pane", async () => {
    open();
    await flushAll();
    emit("session:s1", "mod.pane", { sessionId: "s1", mod: "release-board", id: "board", open: false });

    expect(tabs()).toEqual([]);
    expect(treeOf()).toBeNull();
  });

  it("draws an open pane again on mods.changed, when the first draw came back empty during a reload", async () => {
    drawn = [{ tree: null, mods: [] }, { tree: text("Release 2.4"), mods: [{ name: "release-board", draft: false }] }];
    open();
    await flushAll();
    expect(treeOf()).toBeNull();

    emit("sessions", "mods.changed", {});
    await flushAll();

    expect(renders()).toHaveLength(2);
    expect(treeOf()).toEqual(text("Release 2.4"));
  });

  it("draws an open pane again when its mod redraws, but not for another mod's redraw", async () => {
    open();
    await flushAll();

    emit("session:s1", "mod.ui", { sessionId: "s1", mod: "other-mod" });
    await flushAll();
    expect(renders()).toHaveLength(1);

    emit("session:s1", "mod.ui", { sessionId: "s1", mod: "release-board" });
    await flushAll();
    expect(renders()).toHaveLength(2);
  });
});
