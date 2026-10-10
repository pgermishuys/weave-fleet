import { closeModPane, openModPane, updateModPane } from "@/lib/mods/panes";
import { modPanes, type ModPaneView } from "@/lib/mods/points";
import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { computed, defineComponent, h, ref, type DefineComponent } from "vue";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import CanvasHostComponent from "@/components/canvas/CanvasHost.vue";
import type { FileDiffItem } from "@/api/client";
import { useCanvasesStore } from "@/stores/canvases";

const CanvasHost = CanvasHostComponent as unknown as DefineComponent<{ sessionId: string }>;

function stub(name: string) {
  return { default: defineComponent({ name, setup: () => () => h("div", { class: `stub-${name}` }) }) };
}
vi.mock("@/components/canvas/ChangesCanvas.vue", () => stub("ChangesCanvas"));
vi.mock("@/components/canvas/FilesCanvas.vue", () => stub("FilesCanvas"));
vi.mock("@/components/canvas/VisualCanvas.vue", () => stub("VisualCanvas"));
vi.mock("@/components/session-context/SessionContextCanvas.vue", () => stub("SessionContextCanvas"));
vi.mock("@/components/canvas/FileCanvas.vue", () => stub("FileCanvas"));
vi.mock("@/composables/use-server-canvases", () => ({ closeServerCanvas: vi.fn() }));

const sharedDiffs = {
  diffs: ref<FileDiffItem[]>([]),
  byFile: computed((): ReadonlyMap<string, FileDiffItem> => new Map()),
};

function mountHost() {
  return mount(CanvasHost, { props: { sessionId: "s1" }, global: { provide: { sharedDiffs } }, attachTo: document.body });
}

const pane = (draft = false, paneId = "runs", title = "Test runs", mod = "ci-mod"): ModPaneView => ({
  sessionId: "s1",
  paneId,
  title,
  mod,
  tree: { type: "Text", props: {}, children: ["3 passing"] } as never,
  mods: [{ name: "ci-mod", draft }] as never,
});

describe("CanvasHost tabs", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    globalThis.localStorage?.clear();
    modPanes.clear();
  });
  afterEach(() => modPanes.clear());

  it("shows a mod pane's title in its tab, with the Draft mark only for a draft", async () => {
    const wrapper = mountHost();
    openModPane(pane(false));
    await flushPromises();
    const tab = () => wrapper.findAll('[role="tab"]').at(-1)!;
    expect(tab().find(".canvas-tab__label").text()).toBe("Test runs");
    expect(tab().attributes("aria-selected")).toBe("true");
    expect(tab().find("[data-testid=mod-draft-mark]").exists()).toBe(false);
    expect(wrapper.find("[data-testid=mod-tree]").text()).toContain("3 passing");

    openModPane(pane(true));
    await flushPromises();
    expect(wrapper.findAll('[role="tab"]')).toHaveLength(3);
    expect(tab().find("[data-testid=mod-draft-mark]").exists()).toBe(true);
    expect(wrapper.findAll("[data-testid=mod-draft-mark]")).toHaveLength(1);

    closeModPane("s1", "ci-mod", "runs");
    await flushPromises();
    expect(wrapper.findAll('[role="tab"]')).toHaveLength(2);
    expect(useCanvasesStore().sessionCanvases("s1").canvases.some((c) => c.kind === "mod")).toBe(false);
    wrapper.unmount();
  });

  it("closing a pane's tab with its × drops the pane's view", async () => {
    const wrapper = mountHost();
    openModPane(pane(false));
    await flushPromises();
    await wrapper.findAll('[role="tab"]').at(-1)!.get(".canvas-tab__close").trigger("click");
    await flushPromises();
    expect(wrapper.findAll('[role="tab"]')).toHaveLength(2);
    expect(modPanes.items.value).toHaveLength(0);
    wrapper.unmount();
  });

  it("redrawing a pane with an update keeps the tab where the user is", async () => {
    const wrapper = mountHost();
    openModPane(pane(false));
    await flushPromises();
    await wrapper.findAll('[role="tab"]')[0]!.trigger("click");
    updateModPane(pane(true, "runs", "Test runs again"));
    await flushPromises();
    expect(wrapper.findAll('[role="tab"]')[0]!.attributes("aria-selected")).toBe("true");
    expect(wrapper.findAll('[role="tab"]').at(-1)!.find(".canvas-tab__label").text()).toBe("Test runs again");
    expect(wrapper.findAll("[data-testid=mod-draft-mark]")).toHaveLength(1);
    wrapper.unmount();
  });

  it("two mods with the same pane id draw their own pane in their own tab", async () => {
    const wrapper = mountHost();
    openModPane({ ...pane(false, "runs", "One", "mod-one"), tree: { type: "Text", props: {}, children: ["from one"] } as never });
    openModPane({ ...pane(false, "runs", "Two", "mod-two"), tree: { type: "Text", props: {}, children: ["from two"] } as never });
    await flushPromises();
    expect(wrapper.findAll('[role="tab"]')).toHaveLength(4);
    expect(wrapper.get("[data-testid=mod-tree]").text()).toContain("from two");
    await wrapper.findAll('[role="tab"]')[2]!.trigger("click");
    await flushPromises();
    expect(wrapper.get("[data-testid=mod-tree]").text()).toContain("from one");
    wrapper.unmount();
  });
});
