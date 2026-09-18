import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { computed, ref } from "vue";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import ChangesCanvas from "@/components/canvas/ChangesCanvas.vue";
import type { FileDiffItem } from "@/api/client";
import { useCanvasesStore } from "@/stores/canvases";

const { readSessionFileMock } = vi.hoisted(() => ({
  readSessionFileMock: vi.fn(),
}));

vi.mock("@/api/session-files", () => ({
  browseSessionDirectory: vi.fn(),
  readSessionFile: readSessionFileMock,
}));

const sharedDiffs = {
  diffs: ref<FileDiffItem[]>([]),
  byFile: computed((): ReadonlyMap<string, FileDiffItem> => new Map(sharedDiffs.diffs.value.map((diff) => [diff.file, diff]))),
};

// trigger() can't set `detail` (a getter); dispatch a real MouseEvent with it instead.
function click(element: Element, detail: number) {
  element.dispatchEvent(new MouseEvent("click", { bubbles: true, detail }));
}

function mountCanvas() {
  return mount(ChangesCanvas, {
    props: { sessionId: "s1" },
    global: {
      provide: { sharedDiffs },
    },
  });
}

describe("ChangesCanvas", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    localStorage.clear();
    readSessionFileMock.mockReset();
    sharedDiffs.diffs.value = [];
  });

  it("says so when the session has no changes", async () => {
    const wrapper = mountCanvas();
    await flushPromises();

    expect(wrapper.text()).toContain("No changes in this session yet.");
  });

  it("lists changed files sorted by path with their line counts", async () => {
    sharedDiffs.diffs.value = [
      { file: "src/components/App.vue", status: "modified", additions: 3, deletions: 1 },
      { file: "README.md", status: "added", additions: 5, deletions: 0 },
    ] as FileDiffItem[];

    const wrapper = mountCanvas();
    await flushPromises();

    const rows = wrapper.findAll(".changes-canvas__change");
    expect(rows).toHaveLength(2);
    expect(rows[0]?.get(".changes-canvas__change-name").text()).toBe("README.md");
    expect(rows[1]?.get(".changes-canvas__change-name").text()).toBe("App.vue");
    expect(rows[1]?.get(".changes-canvas__change-dir").text()).toBe("src/components");
    expect(rows[1]?.get(".changes-canvas__change-stats").text()).toBe("+3−1");
  });

  describe("opening a change", () => {
    beforeEach(() => {
      vi.useFakeTimers();
      sharedDiffs.diffs.value = [
        { file: "src/a.ts", status: "modified", additions: 1, deletions: 1 },
        { file: "src/b.ts", status: "modified", additions: 2, deletions: 0 },
      ] as FileDiffItem[];
    });

    afterEach(() => vi.useRealTimers());

    const fileTabs = () =>
      useCanvasesStore().sessionCanvases("s1").canvases.flatMap((canvas) => (canvas.file ? [canvas.file] : []));

    it("opens a preview tab in Diff once a double-click can't follow, and the next file replaces it", async () => {
      const wrapper = mountCanvas();
      await flushPromises();
      const rows = wrapper.findAll(".changes-canvas__change");

      click(rows[0]!.element, 1);
      expect(useCanvasesStore().sessionCanvases("s1").activeId).toBe("changes");

      vi.advanceTimersByTime(250);
      expect(useCanvasesStore().sessionCanvases("s1").activeId).toBe("file:src/a.ts");
      expect(fileTabs()).toEqual([{ path: "src/a.ts", preview: true, view: "diff" }]);
      expect(readSessionFileMock).not.toHaveBeenCalled();
      await flushPromises();
      expect(rows[0]!.classes()).toContain("changes-canvas__change--open");

      click(rows[1]!.element, 1);
      vi.advanceTimersByTime(250);
      expect(fileTabs()).toEqual([{ path: "src/b.ts", preview: true, view: "diff" }]);
    });

    it("a double-click opens a kept tab, which the next file opens beside", async () => {
      const wrapper = mountCanvas();
      await flushPromises();
      const rows = wrapper.findAll(".changes-canvas__change");

      click(rows[0]!.element, 1);
      click(rows[0]!.element, 2);
      click(rows[1]!.element, 1);
      vi.advanceTimersByTime(500);

      expect(fileTabs()).toEqual([
        { path: "src/a.ts", preview: false, view: "diff" },
        { path: "src/b.ts", preview: true, view: "diff" },
      ]);
    });

    it("opens straight away from the keyboard", async () => {
      const wrapper = mountCanvas();
      await flushPromises();

      await wrapper.get(".changes-canvas__change").trigger("click");

      expect(fileTabs()).toEqual([{ path: "src/a.ts", preview: true, view: "diff" }]);
    });
  });
});
