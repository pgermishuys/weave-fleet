import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { computed, nextTick, ref, shallowRef, type DefineComponent } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import FileBrowserPanelComponent from "@/components/session/FileBrowserPanel.vue";
import type { BrowseDirectoryEntry, FileDiffItem } from "@/api/client";
import { useCanvasesStore } from "@/stores/canvases";
import { useFindInFilesStore } from "@/stores/find-in-files";
import { useGoToFileStore } from "@/stores/go-to-file";
import type { FindTextFailure } from "@/composables/use-find-text";
import type { TextSearchResult } from "@/lib/find-text";

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

const mockFindText = {
  result: shallowRef<TextSearchResult | null>(null),
  failure: shallowRef<FindTextFailure | null>(null),
  isLoading: ref(false),
  tookMs: ref(200),
  isActive: ref(true),
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

vi.mock("@/composables/use-find-text", () => ({
  FIND_TEXT_MIN_CHARS: 2,
  useFindText: () => mockFindText,
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
    mockFindText.result.value = null;
    mockFindText.failure.value = null;
    mockFindText.isLoading.value = false;
    mockFindText.isActive.value = true;
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

  describe("Find in files", () => {
    const answer: TextSearchResult = {
      files: [
        {
          path: "src/Retry.cs",
          total: 7,
          matches: [3, 4, 5, 6, 7, 8, 9].map((line) => ({
            line,
            column: 11,
            text: "const int MaxAttempts = 5;",
            ranges: [{ start: 10, end: 21 }],
          })),
        },
        { path: "docs/retries.md", total: 1, matches: [{ line: 2, column: 1, text: "MaxAttempts", ranges: [{ start: 0, end: 11 }] }] },
      ],
      matches: 8,
      stopped: null,
      engine: "git",
    };

    async function searchContents(wrapper: ReturnType<typeof mountPanel>, text = "MaxAttempts") {
      await wrapper.findAll(".file-browser-panel__modes button")[1].trigger("click");
      await wrapper.get(".file-browser-panel__search-input").setValue(text);
      await flushPromises();
    }

    it("starts_in_file_names_and_changes_nothing_until_contents_is_picked", async () => {
      const wrapper = mountPanel();

      expect(wrapper.get(".file-browser-panel__search-input").attributes("placeholder")).toBe("Search files...");
      expect(wrapper.find(".file-browser-panel__toggle").exists()).toBe(false);
      expect(wrapper.findAll(".file-browser-panel__modes button")[0].attributes("aria-pressed")).toBe("true");
    });

    it("shows_the_toggles_and_a_hint_in_contents_mode_before_anything_is_typed", async () => {
      mockFindText.isActive.value = false;
      const wrapper = mountPanel();
      await wrapper.findAll(".file-browser-panel__modes button")[1].trigger("click");

      expect(wrapper.get(".file-browser-panel__search-input").attributes("placeholder")).toBe("Search inside files…");
      expect(wrapper.findAll(".file-browser-panel__toggle").map((toggle) => toggle.text())).toEqual(["Aa", ".*"]);
      expect(wrapper.get(".file-browser-panel__find-hint").text()).toContain("Type to search inside every file");
    });

    it("groups_matches_by_file_shows_five_lines_and_expands_the_rest_on_request", async () => {
      mockFindText.result.value = answer;
      const wrapper = mountPanel();
      await searchContents(wrapper);

      expect(wrapper.text()).toContain("8 matches in 2 files");
      expect(wrapper.findAll(".find-results__file").map((file) => file.text())).toEqual([
        expect.stringContaining("Retry.cs"),
        expect.stringContaining("retries.md"),
      ]);
      expect(wrapper.findAll(".find-results__hit")).toHaveLength(6);
      expect(wrapper.get("mark").text()).toBe("MaxAttempts");

      await wrapper.get(".find-results__more").trigger("click");

      expect(wrapper.text()).not.toContain("more in this file");
      expect(wrapper.findAll(".find-results__hit")).toHaveLength(8);
    });

    it("opens_a_clicked_line_in_a_preview_tab_and_asks_the_editor_to_go_to_it", async () => {
      mockFindText.result.value = answer;
      const wrapper = mountPanel();
      const canvases = useCanvasesStore();
      const openFile = vi.spyOn(canvases, "openFile");
      await searchContents(wrapper);

      await wrapper.findAll(".find-results__hit")[1].trigger("click");

      expect(useGoToFileStore().focusOnOpen).toEqual({ sessionId: "session-1", path: "src/Retry.cs", line: 4 });
      expect(openFile).toHaveBeenCalledWith("session-1", "src/Retry.cs", { view: undefined });
    });

    it("steps_with_the_arrow_keys_and_opens_the_chosen_line_on_enter", async () => {
      mockFindText.result.value = answer;
      const wrapper = mountPanel();
      const openFile = vi.spyOn(useCanvasesStore(), "openFile");
      await searchContents(wrapper);
      const input = wrapper.get(".file-browser-panel__search-input");

      await input.trigger("keydown", { key: "ArrowDown" });
      await input.trigger("keydown", { key: "ArrowDown" });
      await input.trigger("keydown", { key: "Enter" });

      expect(useGoToFileStore().focusOnOpen).toMatchObject({ path: "src/Retry.cs", line: 5 });
      expect(openFile).toHaveBeenCalledTimes(1);
    });

    it("says_so_when_nothing_matches_and_never_when_the_search_failed", async () => {
      mockFindText.result.value = { files: [], matches: 0, stopped: null, engine: "git" };
      const wrapper = mountPanel();
      await searchContents(wrapper, "zzzz");
      expect(wrapper.text()).toContain("No matches for “zzzz”.");

      mockFindText.result.value = { files: [], matches: 0, stopped: "deadline", engine: "git" };
      await nextTick();
      expect(wrapper.text()).not.toContain("No matches");
      expect(wrapper.text()).toContain("ran out of time before it found anything");

      mockFindText.result.value = null;
      mockFindText.failure.value = { kind: "pattern", message: "Unmatched [ (git extended regex)" };
      await nextTick();
      expect(wrapper.text()).toContain("That isn't a valid pattern.");
      expect(wrapper.text()).toContain("Unmatched [ (git extended regex)");

      mockFindText.failure.value = { kind: "unsupported", message: "x" };
      await nextTick();
      expect(wrapper.text()).toContain("can't search inside files yet");
    });

    it("says_when_the_results_hit_the_cap_or_the_folder_is_not_a_git_repository", async () => {
      mockFindText.result.value = { ...answer, stopped: "limit", matches: 200, engine: "walk" };
      const wrapper = mountPanel();
      await searchContents(wrapper);

      expect(wrapper.text()).toContain("First 200 matches, in 2 files");
      expect(wrapper.text()).toContain("Showing the first 200 matches.");
      expect(wrapper.text()).toContain("not a git repository");
    });

    it("keeps_the_query_and_results_when_switching_back_and_forth", async () => {
      mockFindText.result.value = answer;
      const wrapper = mountPanel();
      await searchContents(wrapper);
      await wrapper.findAll(".file-browser-panel__modes button")[0].trigger("click");
      await wrapper.findAll(".file-browser-panel__modes button")[1].trigger("click");

      expect((wrapper.get(".file-browser-panel__search-input").element as HTMLInputElement).value).toBe("MaxAttempts");
      expect(wrapper.findAll(".find-results__file")).toHaveLength(2);
    });

    it("switches_to_contents_and_focuses_the_field_when_ctrl_shift_f_asks", async () => {
      const wrapper = mountPanel();
      const input = wrapper.get(".file-browser-panel__search-input").element as HTMLInputElement;
      const focus = vi.spyOn(input, "focus");

      useFindInFilesStore().ask("session-1");
      await flushPromises();

      expect(wrapper.findAll(".file-browser-panel__modes button")[1].attributes("aria-pressed")).toBe("true");
      expect(focus).toHaveBeenCalled();
      expect(useFindInFilesStore().request).toBeNull();
    });

    it("leaves_a_request_for_another_session_alone", async () => {
      const wrapper = mountPanel();

      useFindInFilesStore().ask("session-2");
      await flushPromises();

      expect(wrapper.findAll(".file-browser-panel__modes button")[0].attributes("aria-pressed")).toBe("true");
      expect(useFindInFilesStore().request).toEqual({ sessionId: "session-2" });
    });
  });
});
