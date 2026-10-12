import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { computed, defineComponent, h, ref, type DefineComponent } from "vue";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import CanvasHostComponent from "@/components/canvas/CanvasHost.vue";
import type { FileDiffItem } from "@/api/client";
import { modPanes } from "@/lib/mods/points";
import { resetModRenders } from "@/lib/mods/renders";
import { useCanvasesStore } from "@/stores/canvases";

const CanvasHost = CanvasHostComponent as unknown as DefineComponent<{ sessionId: string }>;

const { fetchMock } = vi.hoisted(() => ({ fetchMock: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => ({ ...(await importOriginal<object>()), apiFetchOn: fetchMock }));

function stub(name: string) {
  return { default: defineComponent({ name, setup: () => () => h("div") }) };
}
vi.mock("@/components/canvas/ChangesCanvas.vue", () => stub("ChangesCanvas"));
vi.mock("@/components/canvas/FilesCanvas.vue", () => stub("FilesCanvas"));
vi.mock("@/components/canvas/VisualCanvas.vue", () => stub("VisualCanvas"));
vi.mock("@/components/session-context/SessionContextCanvas.vue", () => stub("SessionContextCanvas"));
vi.mock("@/components/canvas/FileCanvas.vue", () => stub("FileCanvas"));
vi.mock("@/composables/use-server-canvases", () => ({ closeServerCanvas: vi.fn() }));
// The menu's own open/close isn't under test: its items are drawn in place and `select` is a click.
vi.mock("@/components/ui/dropdown-menu", () => {
  const part = (tag: string) => defineComponent({ emits: ["select"], setup: (_, { slots, emit, attrs }) => () => h(tag, { ...attrs, onClick: () => emit("select") }, slots.default?.()) });
  return { DropdownMenu: part("div"), DropdownMenuContent: part("div"), DropdownMenuItem: part("button"), DropdownMenuLabel: part("div"), DropdownMenuSeparator: part("hr"), DropdownMenuTrigger: part("div") };
});

const sharedDiffs = { diffs: ref<FileDiffItem[]>([]), byFile: computed((): ReadonlyMap<string, FileDiffItem> => new Map()) };
const mountHost = () => mount(CanvasHost, { props: { sessionId: "s1" }, global: { provide: { sharedDiffs } }, attachTo: document.body });
const answer = (body: unknown) => new Response(JSON.stringify(body), { status: 200 });

const PANES = [
  { mod: "release-board", id: "board", title: "Release board", icon: "gauge", draft: false, off: false },
  { mod: "ci-watch", id: "runs", title: "CI runs", icon: "zap", draft: false, off: true },
  { mod: "test-digest", id: "summary", title: "Test summary", icon: null, draft: true, off: false },
];
const row = (wrapper: ReturnType<typeof mountHost>, mod: string, id: string) => wrapper.get(`[data-testid=canvas-picker-mod-${mod}-${id}]`);

describe("CanvasHost + menu: From mods", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    globalThis.localStorage?.clear();
    modPanes.clear();
    resetModRenders();
    fetchMock.mockReset().mockImplementation(async (_machine: unknown, path: string) =>
      answer(path.endsWith("/mods/sites") ? { sites: [], panes: PANES } : { tree: { type: "Text", props: {}, children: ["Board"] }, mods: [{ name: "release-board", draft: false }] }));
  });
  afterEach(() => modPanes.clear());

  it("lists each declared pane with its mod's name, a Draft mark for a draft, and Off for a mod that is off", async () => {
    const wrapper = mountHost();
    await flushPromises();
    expect(wrapper.text()).toContain("From mods");
    expect(row(wrapper, "release-board", "board").text()).toBe("Release boardrelease-board");
    expect(row(wrapper, "ci-watch", "runs").text()).toBe("CI runsOff");
    expect(row(wrapper, "test-digest", "summary").find("[data-testid=mod-draft-mark]").exists()).toBe(true);
    expect(row(wrapper, "release-board", "board").find("[data-testid=mod-draft-mark]").exists()).toBe(false);
  });

  it("has no From mods group when no mod declares a pane", async () => {
    fetchMock.mockImplementation(async () => answer({ sites: [], panes: [] }));
    const wrapper = mountHost();
    await flushPromises();
    expect(wrapper.text()).not.toContain("From mods");
  });

  it("opens the pane's tab and asks its mod to draw it; the row then says Open", async () => {
    const wrapper = mountHost();
    await flushPromises();
    await row(wrapper, "release-board", "board").trigger("click");
    await flushPromises();
    expect(useCanvasesStore().sessionCanvases("s1").canvases.some((canvas) => canvas.modPane?.paneId === "board")).toBe(true);
    const render = fetchMock.mock.calls.find(([, path]) => String(path).endsWith("/mods/render"))!;
    expect(JSON.parse((render[2] as RequestInit).body as string)).toMatchObject({ site: "Pane", requestId: "board", mod: "release-board" });
    expect(wrapper.find("[data-testid=mod-tree]").text()).toContain("Board");
    expect(row(wrapper, "release-board", "board").text()).toContain("Open");
  });
});
