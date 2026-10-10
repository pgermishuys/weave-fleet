import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { computed, defineComponent, h, ref, type DefineComponent } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import CanvasHostComponent from "@/components/canvas/CanvasHost.vue";
import type { FileDiffItem } from "@/api/client";

/**
 * The canvas tabs as they were before mods: today's DOM. Imports only what existed before mods, so it passes before
 * them and after them alike.
 */
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

/** The DOM without Vue's v-if placeholders and comments. */
const html = (w: { element: Element }) => w.element.outerHTML.replace(/<!--[\s\S]*?-->/g, "");

describe("CanvasHost tabs", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    globalThis.localStorage?.clear();
  });

  it("draws the tabs as they always did", async () => {
    const wrapper = mountHost();
    await flushPromises();
    expect(html(wrapper.get('[role="tablist"]'))).toMatchSnapshot();
    wrapper.unmount();
  });
});
