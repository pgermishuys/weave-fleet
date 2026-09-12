import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { ref } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import ChangesCanvas from "@/components/canvas/ChangesCanvas.vue";
import type { FileDiffItem } from "@/api/client";

const { readSessionFileMock } = vi.hoisted(() => ({
  readSessionFileMock: vi.fn(),
}));

vi.mock("@/api/session-files", () => ({
  browseSessionDirectory: vi.fn(),
  readSessionFile: readSessionFileMock,
}));

const sharedDiffs = {
  diffs: ref<FileDiffItem[]>([]),
};

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
    expect(wrapper.find(".canvas-file-viewer").exists()).toBe(false);
  });

  it("opens the diff for a clicked file and loads its content", async () => {
    sharedDiffs.diffs.value = [
      { file: "src/a.ts", status: "modified", additions: 1, deletions: 1, before: "const a = 1;\n", after: "const a = 2;\n" },
    ] as FileDiffItem[];
    readSessionFileMock.mockResolvedValue({ content: "const a = 2;\n", isBinary: false });

    const wrapper = mountCanvas();
    await flushPromises();

    await wrapper.get(".changes-canvas__change").trigger("click");
    await flushPromises();

    expect(readSessionFileMock).toHaveBeenCalledWith("s1", "src/a.ts");
    expect(wrapper.get(".changes-canvas__change").classes()).toContain("changes-canvas__change--selected");
    expect(wrapper.get(".canvas-file-viewer__label").text()).toBe("Diff:");
    expect(wrapper.get(".canvas-file-viewer__path").text()).toBe("src/a.ts");
    expect(wrapper.get('[aria-label="Show diff"]').attributes("aria-pressed")).toBe("true");
  });

  it("closes the viewer and returns the list to full height", async () => {
    sharedDiffs.diffs.value = [
      { file: "src/a.ts", status: "modified", additions: 1, deletions: 0, before: "", after: "x\n" },
    ] as FileDiffItem[];
    readSessionFileMock.mockResolvedValue({ content: "x\n", isBinary: false });

    const wrapper = mountCanvas();
    await flushPromises();
    await wrapper.get(".changes-canvas__change").trigger("click");
    await flushPromises();

    await wrapper.get('[aria-label="Close file"]').trigger("click");

    expect(wrapper.find(".canvas-file-viewer").exists()).toBe(false);
    expect(wrapper.get(".canvas-split").classes()).not.toContain("canvas-split--with-viewer");
  });
});
