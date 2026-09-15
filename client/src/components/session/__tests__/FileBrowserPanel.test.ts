import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { computed, ref, type DefineComponent } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import FileBrowserPanelComponent from "@/components/session/FileBrowserPanel.vue";
import type { BrowseDirectoryEntry, FileDiffItem } from "@/api/client";

// Mount through a props-only type so @vue/test-utils 2.2.7 (pinned in
// package-lock) accepts the component in mount().
const FileBrowserPanel = FileBrowserPanelComponent as unknown as DefineComponent<{ sessionId: string }>;

const mockFileBrowser = {
  rootEntries: ref<BrowseDirectoryEntry[]>([]),
  expandedDirs: ref(new Map()),
  loadingDirs: ref(new Set()),
  rootLoading: ref(false),
  error: ref<string | null>(null),
  loadRoot: vi.fn(),
  expandDirectory: vi.fn(),
  collapseDirectory: vi.fn(),
  isExpanded: vi.fn(() => false),
  isLoading: vi.fn(() => false),
  selectFile: vi.fn(),
  refresh: vi.fn(),
};

const mockDiffs = {
  diffs: ref<FileDiffItem[]>([]),
  byFile: computed((): ReadonlyMap<string, FileDiffItem> => new Map(mockDiffs.diffs.value.map((diff) => [diff.file, diff]))),
};

const mockFindFiles = {
  files: ref<string[]>([]),
  isLoading: ref(false),
  error: ref<string | null>(null),
};

const mockContentPanel = {
  filesContext: ref({
    selectedFilePath: null as string | null,
    expandedDirs: new Set<string>(),
    searchQuery: "",
    scrollTop: 0,
  }),
  updateFilesContext: vi.fn(),
  selectFile: vi.fn(),
  setViewMode: vi.fn(),
};

vi.mock("@/composables/use-file-browser", () => ({
  useFileBrowser: () => mockFileBrowser,
}));

vi.mock("@/composables/use-diffs", () => ({
  useDiffs: () => mockDiffs,
}));

vi.mock("@/composables/use-find-files", () => ({
  useFindFiles: () => mockFindFiles,
}));

vi.mock("@/composables/use-content-panel", () => ({
  useContentPanelContext: () => mockContentPanel,
}));

describe("FileBrowserPanel", () => {
  function mountPanel() {
    return mount(FileBrowserPanel, {
      props: { sessionId: "session-1" },
      global: {
        provide: {
          sharedDiffs: mockDiffs,
        },
      },
    });
  }

  beforeEach(() => {
    setActivePinia(createPinia());
    vi.clearAllMocks();
    mockFileBrowser.rootEntries.value = [];
    mockFileBrowser.rootLoading.value = false;
    mockFileBrowser.error.value = null;
    mockDiffs.diffs.value = [];
    mockFindFiles.files.value = [];
    mockFindFiles.isLoading.value = false;
    mockFindFiles.error.value = null;
    mockContentPanel.filesContext.value = {
      selectedFilePath: null,
      expandedDirs: new Set(),
      searchQuery: "",
      scrollTop: 0,
    };
  });

  describe("Tree", () => {
    it("shows_every_root_entry_even_when_some_files_changed", async () => {
      mockFileBrowser.rootEntries.value = [
        { relativePath: "src", isDirectory: true, name: "src" },
        { relativePath: "docs", isDirectory: true, name: "docs" },
        { relativePath: "README.md", isDirectory: false, name: "README.md" },
      ] as BrowseDirectoryEntry[];
      mockDiffs.diffs.value = [
        { file: "README.md", status: "added", additions: 5, deletions: 0 },
      ] as FileDiffItem[];

      const wrapper = mountPanel();
      await flushPromises();

      expect(wrapper.findAllComponents({ name: "FileBrowserTreeNode" })).toHaveLength(3);
    });

    it("shows_an_empty_state_when_the_session_has_no_files", async () => {
      const wrapper = mountPanel();
      await flushPromises();

      expect(wrapper.get(".file-browser-panel__empty-text").text()).toBe("No files found");
    });

    it("refreshes_the_tree_from_the_toolbar", async () => {
      const wrapper = mountPanel();
      await flushPromises();

      await wrapper.get('[aria-label="Refresh files"]').trigger("click");

      expect(mockFileBrowser.refresh).toHaveBeenCalledTimes(1);
    });
  });

  describe("File selection", () => {
    it("calls_selectFile_on_content_panel_and_file_browser_when_search_result_clicked", async () => {
      mockFindFiles.files.value = ["src/main.ts", "src/app.ts"];

      const wrapper = mountPanel();

      const searchInput = wrapper.find(".file-browser-panel__search-input");
      await searchInput.setValue("main");
      await flushPromises();

      const resultItems = wrapper.findAll(".file-browser-panel__result-item");
      await resultItems[0]?.trigger("click");

      expect(mockContentPanel.selectFile).toHaveBeenCalledWith("src/main.ts");
      expect(mockFileBrowser.selectFile).toHaveBeenCalledWith("src/main.ts", { keep: false });
    });

    it("opens_the_first_search_result_as_a_kept_tab_on_enter", async () => {
      mockFindFiles.files.value = ["src/main.ts"];

      const wrapper = mountPanel();
      const searchInput = wrapper.find(".file-browser-panel__search-input");
      await searchInput.setValue("main");
      await flushPromises();
      await searchInput.trigger("keydown", { key: "Enter" });

      expect(mockFileBrowser.selectFile).toHaveBeenCalledWith("src/main.ts", { keep: true });
    });
  });
});
