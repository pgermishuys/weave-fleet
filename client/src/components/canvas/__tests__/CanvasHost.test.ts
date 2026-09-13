import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { defineComponent, h, ref, type DefineComponent } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import CanvasHostComponent from "@/components/canvas/CanvasHost.vue";
import type { FileDiffItem } from "@/api/client";
import type { VisualPayload } from "@/lib/visual-payload";
import { serverCanvasTabId, useCanvasesStore, visualCanvasId } from "@/stores/canvases";

// Mount through a props-only type: the named slots on CanvasHost don't fit the
// mount() typings of @vue/test-utils 2.2.7, the version package-lock pins for CI.
const CanvasHost = CanvasHostComponent as unknown as DefineComponent<{ sessionId: string }>;

function stubCanvas(name: string, prop: "sessionId" | "payload") {
  return {
    default: defineComponent({
      name,
      props: { [prop]: { type: null, required: false, default: undefined } },
      setup(props) {
        return () => h("div", { class: `stub-${name}` }, prop === "payload"
          ? (props.payload as VisualPayload | undefined)?.title ?? ""
          : String(props.sessionId ?? ""));
      },
    }),
  };
}

vi.mock("@/components/canvas/ChangesCanvas.vue", () => stubCanvas("ChangesCanvas", "sessionId"));
vi.mock("@/components/canvas/FilesCanvas.vue", () => stubCanvas("FilesCanvas", "sessionId"));
vi.mock("@/components/canvas/VisualCanvas.vue", () => stubCanvas("VisualCanvas", "payload"));
vi.mock("@/components/session-context/SessionContextCanvas.vue", () => stubCanvas("SessionContextCanvas", "sessionId"));

const { closeServerCanvasMock } = vi.hoisted(() => ({ closeServerCanvasMock: vi.fn() }));
vi.mock("@/composables/use-server-canvases", () => ({ closeServerCanvas: closeServerCanvasMock }));

const diagram: VisualPayload = {
  $type: "visual/flow",
  title: "Session event flow",
  content: { nodes: [], edges: [] },
};

const sharedDiffs = {
  diffs: ref<FileDiffItem[]>([]),
};

function mountHost() {
  return mount(CanvasHost, {
    props: { sessionId: "s1" },
    global: {
      provide: { sharedDiffs },
    },
    attachTo: document.body,
  });
}

describe("CanvasHost", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    localStorage.clear();
    sharedDiffs.diffs.value = [];
  });

  it("shows Changes and Files tabs with Changes active and its change count", async () => {
    sharedDiffs.diffs.value = [
      { file: "a.ts", status: "modified", additions: 1, deletions: 0 },
      { file: "b.ts", status: "added", additions: 2, deletions: 0 },
    ] as FileDiffItem[];

    const wrapper = mountHost();
    await flushPromises();

    const tabs = wrapper.findAll('[role="tab"]');
    expect(tabs.map((tab) => tab.find(".canvas-tab__label").text())).toEqual(["Changes", "Files"]);
    expect(tabs[0]?.attributes("aria-selected")).toBe("true");
    expect(tabs[0]?.get(".canvas-tab__count").text()).toBe("2");
    expect(wrapper.find(".stub-ChangesCanvas").text()).toBe("s1");
    wrapper.unmount();
  });

  it("switches canvases when a tab is clicked", async () => {
    const wrapper = mountHost();
    await flushPromises();

    await wrapper.get("#tab-files").trigger("click");

    expect(wrapper.get("#tab-files").attributes("aria-selected")).toBe("true");
    expect(wrapper.find(".stub-FilesCanvas").exists()).toBe(true);
    expect(wrapper.find(".stub-ChangesCanvas").exists()).toBe(false);
    expect(wrapper.get('[role="tabpanel"]').attributes("aria-labelledby")).toBe("tab-files");
    wrapper.unmount();
  });

  it("adds a tab for a visual the agent rendered and closes it again", async () => {
    const wrapper = mountHost();
    const store = useCanvasesStore();
    await flushPromises();

    store.openVisual("s1", diagram);
    await flushPromises();

    const tab = wrapper.findAll('[role="tab"]').at(-1)!;
    expect(tab.find(".canvas-tab__label").text()).toBe("Session event flow");
    expect(tab.attributes("aria-selected")).toBe("true");
    expect(wrapper.get(".stub-VisualCanvas").text()).toBe("Session event flow");

    await tab.get(".canvas-tab__close").trigger("click");

    expect(wrapper.findAll('[role="tab"]')).toHaveLength(2);
    expect(store.sessionCanvases("s1").canvases.some((canvas) => canvas.id === visualCanvasId(diagram))).toBe(false);
    wrapper.unmount();
  });

  it("shows a server canvas read-only and closes it through the server", async () => {
    const wrapper = mountHost();
    const store = useCanvasesStore();
    await flushPromises();

    store.setServerCanvases("s1", [{
      canvasId: "cv_1",
      kind: "diagram",
      title: "Agent diagram",
      version: 3,
      state: { nodes: [{ id: "n1", label: "Hub" }], edges: [] },
    }]);
    store.activate("s1", serverCanvasTabId("cv_1"));
    await flushPromises();

    const tab = wrapper.get(`#tab-canvas-cv_1`);
    expect(tab.find(".canvas-tab__label").text()).toBe("Agent diagram");
    expect(wrapper.get(".stub-VisualCanvas").attributes("readonly")).toBeDefined();

    await tab.get(".canvas-tab__close").trigger("click");

    expect(closeServerCanvasMock).toHaveBeenCalledWith("s1", "cv_1");
    wrapper.unmount();
  });

  it("does not offer to close Changes", async () => {
    const wrapper = mountHost();
    await flushPromises();

    expect(wrapper.get("#tab-changes").find(".canvas-tab__close").exists()).toBe(false);
    expect(wrapper.get("#tab-files").find(".canvas-tab__close").exists()).toBe(true);
    wrapper.unmount();
  });

  it("moves between tabs with the arrow keys", async () => {
    const wrapper = mountHost();
    await flushPromises();

    await wrapper.get("#tab-changes").trigger("keydown", { key: "ArrowRight" });
    expect(wrapper.get("#tab-files").attributes("aria-selected")).toBe("true");

    await wrapper.get("#tab-files").trigger("keydown", { key: "ArrowRight" });
    expect(wrapper.get("#tab-changes").attributes("aria-selected")).toBe("true");

    await wrapper.get("#tab-changes").trigger("keydown", { key: "End" });
    expect(wrapper.get("#tab-files").attributes("aria-selected")).toBe("true");
    wrapper.unmount();
  });

  it("shows an attention dot or a count from tab badges", async () => {
    const HostWithBadges = CanvasHostComponent as unknown as DefineComponent<{
      sessionId: string;
      tabBadges?: Record<string, { count?: number; attention?: boolean; label?: string }>;
    }>;

    const wrapper = mount(HostWithBadges, {
      props: { sessionId: "s1", tabBadges: { context: { count: 3, attention: true, label: "A linked pull request needs attention" } } },
      global: { provide: { sharedDiffs } },
      attachTo: document.body,
    });
    useCanvasesStore().introduce("s1", "context");
    await flushPromises();

    const tab = wrapper.get("#tab-context");
    expect(tab.get(".canvas-tab__alert").attributes("aria-label")).toBe("A linked pull request needs attention");
    expect(tab.find(".canvas-tab__count").exists()).toBe(false);

    await wrapper.setProps({ tabBadges: { context: { count: 3 } } });
    expect(wrapper.get("#tab-context").get(".canvas-tab__count").text()).toBe("3");
    wrapper.unmount();
  });

  it("toggles Widen", async () => {
    const wrapper = mountHost();
    const store = useCanvasesStore();
    await flushPromises();

    const widen = wrapper.get('[aria-label="Widen canvas"]');
    await widen.trigger("click");

    expect(store.widened).toBe(true);
    expect(wrapper.get('[aria-label="Narrow canvas"]').attributes("aria-pressed")).toBe("true");
    wrapper.unmount();
  });

  it("pulses a tab whose page updated itself", async () => {
    const wrapper = mountHost();
    const store = useCanvasesStore();
    await flushPromises();

    store.markUpdated("files");
    await new Promise((resolve) => requestAnimationFrame(() => resolve(null)));
    await flushPromises();

    expect(wrapper.get("#tab-files").classes()).toContain("canvas-tab--updated");
    expect(wrapper.get("#tab-changes").classes()).not.toContain("canvas-tab--updated");
    wrapper.unmount();
  });
});
