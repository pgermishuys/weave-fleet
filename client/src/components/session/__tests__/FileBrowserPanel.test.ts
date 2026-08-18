import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { computed, ref } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import FileBrowserPanel from "@/components/session/FileBrowserPanel.vue";
import type { FileDiffItem } from "@/api/client";

const mockFileBrowser = {
  rootEntries: ref([]),
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

  describe("All/Changed filter toggle", () => {
    it("displays_all_filter_as_active_by_default", async () => {
      const wrapper = mount(FileBrowserPanel, {
        props: { sessionId: "session-1" },
        global: {
          provide: {
            sharedDiffs: mockDiffs,
          },
        },
      });
      await flushPromises();

      const allButton = wrapper.find('[aria-checked="true"]');
      expect(allButton.text()).toBe("All");
      expect(allButton.classes()).toContain("file-browser-panel__filter-option--active");
    });

    it("displays_changed_count_in_changed_filter_button", async () => {
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

      const changedButton = wrapper.findAll(".file-browser-panel__filter-option")[1];
      expect(changedButton?.text()).toBe("Changed (2)");
    });

    it("switches_to_changed_filter_on_click", async () => {
      const wrapper = mount(FileBrowserPanel, {
        props: { sessionId: "session-1" },
        global: {
          provide: {
            sharedDiffs: mockDiffs,
          },
        },
      });
      await flushPromises();

      const changedButton = wrapper.findAll(".file-browser-panel__filter-option")[1];
      await changedButton?.trigger("click");

      expect(mockContentPanel.updateFilesContext).toHaveBeenCalledWith({
        allChangedFilter: "changed",
      });
    });

    it("filters_tree_to_show_only_changed_files_and_ancestors", async () => {
      mockFileBrowser.rootEntries.value = [
        { relativePath: "src", isDirectory: true, name: "src" },
        { relativePath: "docs", isDirectory: true, name: "docs" },
        { relativePath: "README.md", isDirectory: false, name: "README.md" },
      ] as any[];

      mockDiffs.diffs.value = [
        { file: "src/components/App.vue", status: "modified", additions: 1, deletions: 0 },
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

      // Only "src" directory should be visible (ancestor of changed file)
      const treeNodes = wrapper.findAllComponents({ name: "FileBrowserTreeNode" });
      expect(treeNodes).toHaveLength(1);
      expect(treeNodes[0]?.props("entry").relativePath).toBe("src");
    });

    it("shows_all_files_when_all_filter_is_active", async () => {
      mockFileBrowser.rootEntries.value = [
        { relativePath: "src", isDirectory: true, name: "src" },
        { relativePath: "docs", isDirectory: true, name: "docs" },
        { relativePath: "README.md", isDirectory: false, name: "README.md" },
      ] as any[];

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
