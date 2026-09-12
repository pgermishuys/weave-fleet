import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { ref } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import FileBrowserPanel from "@/components/session/FileBrowserPanel.vue";
import type { BrowseDirectoryEntry, FileDiffItem } from "@/api/client";

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
};

const mockFindFiles = {
  files: ref<string[]>([]),
  isLoading: ref(false),
  error: ref<string | null>(null),
};

const mockContentPanel = {
  filesContext: ref({
    allChangedFilter: "all" as "all" | "changed",
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
      allChangedFilter: "all",
      selectedFilePath: null,
      expandedDirs: new Set(),
      searchQuery: "",
      scrollTop: 0,
    };
  });

  describe("Changes/Files tabs", () => {
    it("displays_files_tab_as_active_by_default", async () => {
      const wrapper = mount(FileBrowserPanel, {
        props: { sessionId: "session-1" },
        global: {
          provide: {
            sharedDiffs: mockDiffs,
          },
        },
      });
      await flushPromises();

      const filesTab = wrapper.find('[role="tab"][aria-selected="true"]');
      expect(filesTab.text()).toBe("Files");
      expect(filesTab.classes()).toContain("file-browser-panel__filter-option--active");
    });

    it("displays_changed_count_in_changes_tab", async () => {
      mockDiffs.diffs.value = [
        { file: "src/a.ts", status: "modified", additions: 1, deletions: 0 },
        { file: "src/b.ts", status: "added", additions: 5, deletions: 0 },
      ] as FileDiffItem[];

      const wrapper = mount(FileBrowserPanel, {
        props: { sessionId: "session-1" },
        global: {
          provide: {
            sharedDiffs: mockDiffs,
          },
        },
      });
      await flushPromises();

      const changesTab = wrapper.findAll(".file-browser-panel__filter-option")[0];
      expect(changesTab?.text()).toBe("Changes 2");
      expect(changesTab?.get(".file-browser-panel__count").text()).toBe("2");
    });

    it("switches_to_changes_tab_on_click", async () => {
      const wrapper = mount(FileBrowserPanel, {
        props: { sessionId: "session-1" },
        global: {
          provide: {
            sharedDiffs: mockDiffs,
          },
        },
      });
      await flushPromises();

      const changesTab = wrapper.findAll(".file-browser-panel__filter-option")[0];
      await changesTab?.trigger("click");

      expect(mockContentPanel.updateFilesContext).toHaveBeenCalledWith({
        allChangedFilter: "changed",
      });
    });

    it("lists_changed_files_flat_with_line_counts_in_changes_tab", async () => {
      mockFileBrowser.rootEntries.value = [
        { relativePath: "src", isDirectory: true, name: "src" },
        { relativePath: "docs", isDirectory: true, name: "docs" },
        { relativePath: "README.md", isDirectory: false, name: "README.md" },
      ] as BrowseDirectoryEntry[];

      mockDiffs.diffs.value = [
        { file: "src/components/App.vue", status: "modified", additions: 3, deletions: 1 },
        { file: "README.md", status: "added", additions: 5, deletions: 0 },
      ] as FileDiffItem[];

      mockContentPanel.filesContext.value.allChangedFilter = "changed";

      const wrapper = mount(FileBrowserPanel, {
        props: { sessionId: "session-1" },
        global: {
          provide: {
            sharedDiffs: mockDiffs,
          },
        },
      });
      await flushPromises();

      // The Changes tab lists changed files (sorted by path) instead of the tree
      expect(wrapper.findAllComponents({ name: "FileBrowserTreeNode" })).toHaveLength(0);
      const rows = wrapper.findAll(".file-browser-panel__change");
      expect(rows).toHaveLength(2);
      expect(rows[0]?.get(".file-browser-panel__change-name").text()).toBe("README.md");
      expect(rows[1]?.get(".file-browser-panel__change-name").text()).toBe("App.vue");
      expect(rows[1]?.get(".file-browser-panel__change-dir").text()).toBe("src/components");
      expect(rows[1]?.get(".file-browser-panel__change-stats").text()).toBe("+3−1");
    });

    it("opens_the_diff_when_a_changed_file_is_clicked", async () => {
      mockDiffs.diffs.value = [
        { file: "src/a.ts", status: "modified", additions: 1, deletions: 1 },
      ] as FileDiffItem[];
      mockContentPanel.filesContext.value.allChangedFilter = "changed";

      const wrapper = mount(FileBrowserPanel, {
        props: { sessionId: "session-1" },
        global: {
          provide: {
            sharedDiffs: mockDiffs,
          },
        },
      });
      await flushPromises();

      await wrapper.get(".file-browser-panel__change").trigger("click");

      expect(mockContentPanel.selectFile).toHaveBeenCalledWith("src/a.ts");
      expect(mockContentPanel.setViewMode).toHaveBeenCalledWith("diff");
      expect(mockFileBrowser.selectFile).toHaveBeenCalledWith("src/a.ts");
    });

    it("shows_all_files_when_all_filter_is_active", async () => {
      mockFileBrowser.rootEntries.value = [
        { relativePath: "src", isDirectory: true, name: "src" },
        { relativePath: "docs", isDirectory: true, name: "docs" },
        { relativePath: "README.md", isDirectory: false, name: "README.md" },
      ] as BrowseDirectoryEntry[];

      mockContentPanel.filesContext.value.allChangedFilter = "all";

      const wrapper = mount(FileBrowserPanel, {
        props: { sessionId: "session-1" },
        global: {
          provide: {
            sharedDiffs: mockDiffs,
          },
        },
      });
      await flushPromises();

      const treeNodes = wrapper.findAllComponents({ name: "FileBrowserTreeNode" });
      expect(treeNodes).toHaveLength(3);
    });
  });

  describe("File selection", () => {
    it("calls_selectFile_on_content_panel_and_file_browser_when_search_result_clicked", async () => {
      mockFindFiles.files.value = ["src/main.ts", "src/app.ts"];

      const wrapper = mount(FileBrowserPanel, {
        props: { sessionId: "session-1" },
        global: {
          provide: {
            sharedDiffs: mockDiffs,
          },
        },
      });

      // Trigger search
      const searchInput = wrapper.find(".file-browser-panel__search-input");
      await searchInput.setValue("main");
      await flushPromises();

      const resultItems = wrapper.findAll(".file-browser-panel__result-item");
      await resultItems[0]?.trigger("click");

      expect(mockContentPanel.selectFile).toHaveBeenCalledWith("src/main.ts");
      expect(mockFileBrowser.selectFile).toHaveBeenCalledWith("src/main.ts");
    });
  });
});
