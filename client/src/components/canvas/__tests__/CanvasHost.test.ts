import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { computed, defineComponent, h, ref, type DefineComponent } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import CanvasHostComponent from "@/components/canvas/CanvasHost.vue";
import type { FileDiffItem } from "@/api/client";
import type { VisualPayload } from "@/lib/visual-payload";
import { serverCanvasTabId, useCanvasesStore, visualCanvasId } from "@/stores/canvases";
import { useFileBuffersStore } from "@/stores/file-buffers";

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
vi.mock("@/components/canvas/FileCanvas.vue", () => ({
  default: defineComponent({
    name: "FileCanvas",
    props: { sessionId: String, path: String, view: String },
    setup(props) {
      return () => h("div", { class: "stub-FileCanvas" }, `${props.path} ${props.view}`);
    },
  }),
}));

// The real dialog renders through reka-ui's portal; a stub shows its buttons while it's open.
vi.mock("@/components/canvas/UnsavedFileDialog.vue", () => ({
  default: defineComponent({
    name: "UnsavedFileDialog",
    props: { open: Boolean, path: String, saving: Boolean },
    emits: ["save", "discard", "update:open"],
    setup(props, { emit }) {
      return () => props.open
        ? h("div", { class: "stub-unsaved" }, [
          h("p", `Save changes before closing? ${props.path}`),
          h("button", { "data-testid": "unsaved-save", onClick: () => emit("save") }, "Save and close"),
          h("button", { "data-testid": "unsaved-discard", onClick: () => emit("discard") }, "Close without saving"),
        ])
        : null;
    },
  }),
}));

const { saveBufferMock } = vi.hoisted(() => ({ saveBufferMock: vi.fn() }));
vi.mock("@/lib/code-editor/buffers", () => ({ saveBuffer: saveBufferMock }));

const { closeServerCanvasMock } = vi.hoisted(() => ({ closeServerCanvasMock: vi.fn() }));
vi.mock("@/composables/use-server-canvases", () => ({ closeServerCanvas: closeServerCanvasMock }));

const diagram: VisualPayload = {
  $type: "visual/flow",
  title: "Session event flow",
  content: { nodes: [], edges: [] },
};

const sharedDiffs = {
  diffs: ref<FileDiffItem[]>([]),
  byFile: computed((): ReadonlyMap<string, FileDiffItem> => new Map(sharedDiffs.diffs.value.map((diff) => [diff.file, diff]))),
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

  it("has no Widen where the panel already fills the screen", async () => {
    const wrapper = mount(CanvasHost, {
      props: { sessionId: "s1", widenable: false } as { sessionId: string },
      global: { provide: { sharedDiffs } },
      attachTo: document.body,
    });
    await flushPromises();

    expect(wrapper.find('[aria-label="Widen canvas"]').exists()).toBe(false);
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

  describe("file tabs", () => {
    async function openedFile(path: string, keep = false) {
      const wrapper = mountHost();
      useCanvasesStore().openFile("s1", path, { keep });
      await flushPromises();
      return { wrapper, tab: () => wrapper.get(`[data-testid="file-tab-${path}"]`) };
    }

    it("shows a file tab by name with its path, and passes the file to the canvas", async () => {
      const { wrapper, tab } = await openedFile("src/app.ts");
      expect(tab().get(".canvas-tab__label").text()).toBe("app.ts");
      expect(tab().attributes("title")).toBe("src/app.ts");
      expect(tab().attributes("aria-selected")).toBe("true");
      expect(wrapper.get(".stub-FileCanvas").text()).toBe("src/app.ts edit");
      wrapper.unmount();
    });

    it("draws a preview tab in italics and keeps it on double-click", async () => {
      const { wrapper, tab } = await openedFile("src/app.ts");
      expect(tab().classes()).toContain("canvas-tab--preview");

      await tab().trigger("dblclick");

      expect(tab().classes()).not.toContain("canvas-tab--preview");
      wrapper.unmount();
    });

    it("shows a dot instead of the close button while the file has unsaved changes", async () => {
      const { wrapper, tab } = await openedFile("src/app.ts", true);
      expect(tab().find(".canvas-tab__unsaved-dot").exists()).toBe(false);

      const buffers = useFileBuffersStore();
      buffers.ensure("s1", "src/app.ts");
      buffers.patch("s1", "src/app.ts", { dirty: true });
      await flushPromises();

      expect(tab().find(".canvas-tab__unsaved-dot").exists()).toBe(true);
      expect(tab().get(".canvas-tab__close").attributes("aria-label")).toBe("Close app.ts (unsaved changes)");
      wrapper.unmount();
    });

    it("closes a clean file straight away", async () => {
      const { wrapper, tab } = await openedFile("src/app.ts", true);
      await tab().get(".canvas-tab__close").trigger("click");
      await flushPromises();
      expect(wrapper.find('[data-testid="file-tab-src/app.ts"]').exists()).toBe(false);
      wrapper.unmount();
    });

    it("asks before closing a file with unsaved changes, and can close without saving", async () => {
      const { wrapper, tab } = await openedFile("src/app.ts", true);
      const buffers = useFileBuffersStore();
      buffers.ensure("s1", "src/app.ts");
      buffers.patch("s1", "src/app.ts", { dirty: true });
      await flushPromises();

      await tab().get(".canvas-tab__close").trigger("click");
      await flushPromises();
      expect(wrapper.get(".stub-unsaved").text()).toContain("Save changes before closing? src/app.ts");
      expect(wrapper.find('[data-testid="file-tab-src/app.ts"]').exists()).toBe(true);

      await wrapper.get('[data-testid="unsaved-discard"]').trigger("click");
      await flushPromises();

      expect(wrapper.find('[data-testid="file-tab-src/app.ts"]').exists()).toBe(false);
      expect(buffers.record("s1", "src/app.ts")).toBeUndefined();
      wrapper.unmount();
    });

    it("saves and closes, or keeps the tab open when the save hits a conflict", async () => {
      const { wrapper, tab } = await openedFile("src/app.ts", true);
      const buffers = useFileBuffersStore();
      buffers.ensure("s1", "src/app.ts");
      buffers.patch("s1", "src/app.ts", { dirty: true });
      await flushPromises();

      saveBufferMock.mockResolvedValueOnce({ kind: "conflict" });
      await tab().get(".canvas-tab__close").trigger("click");
      await flushPromises();
      await wrapper.get('[data-testid="unsaved-save"]').trigger("click");
      await flushPromises();
      expect(saveBufferMock).toHaveBeenCalledWith("s1", "src/app.ts");
      expect(wrapper.find('[data-testid="file-tab-src/app.ts"]').exists()).toBe(true);

      saveBufferMock.mockResolvedValueOnce({ kind: "saved" });
      await tab().get(".canvas-tab__close").trigger("click");
      await flushPromises();
      await wrapper.get('[data-testid="unsaved-save"]').trigger("click");
      await flushPromises();
      expect(wrapper.find('[data-testid="file-tab-src/app.ts"]').exists()).toBe(false);
      wrapper.unmount();
    });
  });
});
