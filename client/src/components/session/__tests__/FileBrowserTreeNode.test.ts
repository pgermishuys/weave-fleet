import { mount } from "@vue/test-utils";
import { ref, type DefineComponent } from "vue";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import FileBrowserTreeNodeComponent from "@/components/session/FileBrowserTreeNode.vue";
import type { BrowseDirectoryEntry } from "@/api/client";

const FileBrowserTreeNode = FileBrowserTreeNodeComponent as unknown as DefineComponent<{ entry: BrowseDirectoryEntry; depth: number; sessionId: string }>;

vi.mock("@/composables/use-content-panel", () => ({
  useContentPanelContext: () => ({ selectFile: vi.fn(), filesContext: ref({ selectedFilePath: null }) }),
}));

const fileBrowser = {
  isExpanded: () => false,
  isLoading: () => false,
  expandedDirs: ref(new Map()),
  selectFile: vi.fn(),
};

// trigger() can't set `detail` (a getter); dispatch a real MouseEvent with it instead.
function click(element: Element, detail: number) {
  element.dispatchEvent(new MouseEvent("click", { bubbles: true, detail }));
}

function mountNode() {
  return mount(FileBrowserTreeNode, {
    props: { entry: { name: "app.ts", relativePath: "src/app.ts", isDirectory: false }, depth: 0, sessionId: "s1" },
    global: { provide: { fileBrowser, diffs: { diffs: ref([]) } } },
  });
}

describe("FileBrowserTreeNode", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    fileBrowser.selectFile.mockReset();
  });

  afterEach(() => vi.useRealTimers());

  it("opens a preview tab after one click, once a double-click can't follow", async () => {
    const wrapper = mountNode();
    click(wrapper.get('[data-testid="file-node-src/app.ts"]').element, 1);
    expect(fileBrowser.selectFile).not.toHaveBeenCalled();

    vi.advanceTimersByTime(250);
    expect(fileBrowser.selectFile).toHaveBeenCalledWith("src/app.ts");
  });

  it("a double-click opens a kept tab, and only that", async () => {
    const wrapper = mountNode();
    const node = wrapper.get('[data-testid="file-node-src/app.ts"]').element;
    click(node, 1);
    click(node, 2);
    vi.advanceTimersByTime(500);

    expect(fileBrowser.selectFile).toHaveBeenCalledTimes(1);
    expect(fileBrowser.selectFile).toHaveBeenCalledWith("src/app.ts", { keep: true });
  });
});
