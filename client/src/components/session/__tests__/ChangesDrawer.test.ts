import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { ref } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import ChangesDrawer from "@/components/session/ChangesDrawer.vue";
import type { FileDiffItem } from "@/api/client";

const mockContentPanel = {
  drawerMode: ref<"collapsed" | "expanded" | "maximized">("collapsed"),
  setDrawerMode: vi.fn((mode: "collapsed" | "expanded" | "maximized") => {
    mockContentPanel.drawerMode.value = mode;
  }),
};

const mockDiffs = {
  diffs: ref<FileDiffItem[]>([]),
  available: ref(true),
  isLoading: ref(false),
  error: ref<string | null>(null),
};

vi.mock("@/composables/use-content-panel", () => ({
  useContentPanelContext: () => mockContentPanel,
}));

vi.mock("@/composables/use-diffs", () => ({
  useDiffs: () => mockDiffs,
}));

describe("ChangesDrawer", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    vi.clearAllMocks();
    mockContentPanel.drawerMode.value = "collapsed";
    mockDiffs.diffs.value = [];
    mockDiffs.available.value = true;
    mockDiffs.isLoading.value = false;
    mockDiffs.error.value = null;
  });

  describe("Drawer mode transitions", () => {
    it("starts_in_collapsed_mode", async () => {
      const wrapper = mount(ChangesDrawer, {
        props: { sessionId: "session-1" },
        global: {
          provide: {
            sharedDiffs: mockDiffs,
          },
        },
      });
      await flushPromises();

      expect(wrapper.classes()).toContain("changes-drawer--collapsed");
      expect(wrapper.attributes("style")).toContain("height: 40px");
    });

    it("transitions_from_collapsed_to_expanded_on_handle_click", async () => {
      const wrapper = mount(ChangesDrawer, {
        props: { sessionId: "session-1" },
        global: {
          provide: {
            sharedDiffs: mockDiffs,
          },
        },
      });
      await flushPromises();

      const handle = wrapper.find(".changes-drawer__handle");
      await handle.trigger("click");

      expect(mockContentPanel.setDrawerMode).toHaveBeenCalledWith("expanded");
      expect(wrapper.classes()).toContain("changes-drawer--expanded");
      expect(wrapper.attributes("style")).toContain("height: 280px");
    });

    it("transitions_from_expanded_to_collapsed_on_handle_click", async () => {
      mockContentPanel.drawerMode.value = "expanded";

      const wrapper = mount(ChangesDrawer, {
        props: { sessionId: "session-1" },
        global: {
          provide: {
            sharedDiffs: mockDiffs,
          },
        },
      });
      await flushPromises();

      const handle = wrapper.find(".changes-drawer__handle");
      await handle.trigger("click");

      expect(mockContentPanel.setDrawerMode).toHaveBeenCalledWith("collapsed");
    });

    it("transitions_from_maximized_to_collapsed_on_handle_click", async () => {
      mockContentPanel.drawerMode.value = "maximized";

      const wrapper = mount(ChangesDrawer, {
        props: { sessionId: "session-1" },
        global: {
          provide: {
            sharedDiffs: mockDiffs,
          },
        },
      });
      await flushPromises();

      const handle = wrapper.find(".changes-drawer__handle");
      await handle.trigger("click");

      expect(mockContentPanel.setDrawerMode).toHaveBeenCalledWith("collapsed");
    });

    it("transitions_from_expanded_to_maximized_on_maximize_button_click", async () => {
      mockContentPanel.drawerMode.value = "expanded";

      const wrapper = mount(ChangesDrawer, {
        props: { sessionId: "session-1" },
        global: {
          provide: {
            sharedDiffs: mockDiffs,
          },
        },
      });
      await flushPromises();

      const maximizeBtn = wrapper.find(".changes-drawer__maximize-btn");
      await maximizeBtn.trigger("click");

      expect(mockContentPanel.setDrawerMode).toHaveBeenCalledWith("maximized");
    });

    it("transitions_from_maximized_to_expanded_on_maximize_button_click", async () => {
      mockContentPanel.drawerMode.value = "maximized";

      const wrapper = mount(ChangesDrawer, {
        props: { sessionId: "session-1" },
        global: {
          provide: {
            sharedDiffs: mockDiffs,
          },
        },
      });
      await flushPromises();

      const maximizeBtn = wrapper.find(".changes-drawer__maximize-btn");
      await maximizeBtn.trigger("click");

      expect(mockContentPanel.setDrawerMode).toHaveBeenCalledWith("expanded");
    });

    it("shows_maximize_button_only_when_not_collapsed", async () => {
      const wrapper = mount(ChangesDrawer, {
        props: { sessionId: "session-1" },
        global: {
          provide: {
            sharedDiffs: mockDiffs,
          },
        },
      });
      await flushPromises();

      expect(wrapper.find(".changes-drawer__maximize-btn").exists()).toBe(false);

      mockContentPanel.drawerMode.value = "expanded";
      await flushPromises();

      expect(wrapper.find(".changes-drawer__maximize-btn").exists()).toBe(true);
    });

    it("sets_height_to_100_percent_when_maximized", async () => {
      mockContentPanel.drawerMode.value = "maximized";

      const wrapper = mount(ChangesDrawer, {
        props: { sessionId: "session-1" },
        global: {
          provide: {
            sharedDiffs: mockDiffs,
          },
        },
      });
      await flushPromises();

      expect(wrapper.classes()).toContain("changes-drawer--maximized");
      expect(wrapper.attributes("style")).toContain("height: 100%");
    });
  });

  describe("No auto-expand on data change", () => {
    it("does_not_expand_drawer_when_diffs_load", async () => {
      const wrapper = mount(ChangesDrawer, {
        props: { sessionId: "session-1" },
        global: {
          provide: {
            sharedDiffs: mockDiffs,
          },
        },
      });
      await flushPromises();

      expect(mockContentPanel.drawerMode.value).toBe("collapsed");

      // Simulate diffs loading
      mockDiffs.diffs.value = [
        {
          file: "src/main.ts",
          status: "modified",
          additions: 5,
          deletions: 2,
          before: "const a = 1;",
          after: "const a = 2;",
        },
      ] as FileDiffItem[];
      await flushPromises();

      // Drawer should remain collapsed
      expect(mockContentPanel.drawerMode.value).toBe("collapsed");
      expect(wrapper.classes()).toContain("changes-drawer--collapsed");
    });

    it("does_not_expand_drawer_when_more_diffs_are_added", async () => {
      mockDiffs.diffs.value = [
        {
          file: "src/main.ts",
          status: "modified",
          additions: 5,
          deletions: 2,
        },
      ] as FileDiffItem[];

      const wrapper = mount(ChangesDrawer, {
        props: { sessionId: "session-1" },
        global: {
          provide: {
            sharedDiffs: mockDiffs,
          },
        },
      });
      await flushPromises();

      expect(mockContentPanel.drawerMode.value).toBe("collapsed");

      // Add more diffs
      mockDiffs.diffs.value = [
        ...mockDiffs.diffs.value,
        {
          file: "src/app.ts",
          status: "added",
          additions: 10,
          deletions: 0,
        },
      ] as FileDiffItem[];
      await flushPromises();

      // Drawer should remain collapsed
      expect(mockContentPanel.drawerMode.value).toBe("collapsed");
    });
  });

  describe("Summary display", () => {
    it("displays_loading_state_in_summary", async () => {
      mockDiffs.isLoading.value = true;

      const wrapper = mount(ChangesDrawer, {
        props: { sessionId: "session-1" },
        global: {
          provide: {
            sharedDiffs: mockDiffs,
          },
        },
      });
      await flushPromises();

      expect(wrapper.find(".changes-drawer__summary").text()).toContain("Loading changes...");
    });

    it("displays_error_state_in_summary", async () => {
      mockDiffs.error.value = "Failed to load diffs";

      const wrapper = mount(ChangesDrawer, {
        props: { sessionId: "session-1" },
        global: {
          provide: {
            sharedDiffs: mockDiffs,
          },
        },
      });
      await flushPromises();

      expect(wrapper.find(".changes-drawer__summary").text()).toContain("Error loading changes");
    });

    it("displays_unavailable_state_in_summary", async () => {
      mockDiffs.available.value = false;

      const wrapper = mount(ChangesDrawer, {
        props: { sessionId: "session-1" },
        global: {
          provide: {
            sharedDiffs: mockDiffs,
          },
        },
      });
      await flushPromises();

      expect(wrapper.find(".changes-drawer__summary").text()).toContain("Changes unavailable");
    });

    it("displays_no_changes_when_diffs_empty", async () => {
      mockDiffs.diffs.value = [];

      const wrapper = mount(ChangesDrawer, {
        props: { sessionId: "session-1" },
        global: {
          provide: {
            sharedDiffs: mockDiffs,
          },
        },
      });
      await flushPromises();

      expect(wrapper.find(".changes-drawer__summary").text()).toContain("No changes");
    });

    it("displays_file_count_and_stats_when_diffs_present", async () => {
      mockDiffs.diffs.value = [
        { file: "src/a.ts", status: "modified", additions: 5, deletions: 2 },
        { file: "src/b.ts", status: "added", additions: 10, deletions: 0 },
      ] as FileDiffItem[];

      const wrapper = mount(ChangesDrawer, {
        props: { sessionId: "session-1" },
        global: {
          provide: {
            sharedDiffs: mockDiffs,
          },
        },
      });
      await flushPromises();

      const summary = wrapper.find(".changes-drawer__summary").text();
      expect(summary).toContain("2 files");
      expect(summary).toContain("+15");
      expect(summary).toContain("-2");
    });

    it("displays_singular_file_label_for_one_file", async () => {
      mockDiffs.diffs.value = [
        { file: "src/a.ts", status: "modified", additions: 5, deletions: 2 },
      ] as FileDiffItem[];

      const wrapper = mount(ChangesDrawer, {
        props: { sessionId: "session-1" },
        global: {
          provide: {
            sharedDiffs: mockDiffs,
          },
        },
      });
      await flushPromises();

      expect(wrapper.find(".changes-drawer__summary").text()).toContain("1 file");
    });
  });

  describe("Content visibility", () => {
    it("hides_content_when_collapsed", async () => {
      const wrapper = mount(ChangesDrawer, {
        props: { sessionId: "session-1" },
        global: {
          provide: {
            sharedDiffs: mockDiffs,
          },
        },
      });
      await flushPromises();

      expect(wrapper.find("#changes-drawer-content").exists()).toBe(false);
    });

    it("shows_content_when_expanded", async () => {
      mockContentPanel.drawerMode.value = "expanded";

      const wrapper = mount(ChangesDrawer, {
        props: { sessionId: "session-1" },
        global: {
          provide: {
            sharedDiffs: mockDiffs,
          },
        },
      });
      await flushPromises();

      expect(wrapper.find("#changes-drawer-content").exists()).toBe(true);
    });

    it("shows_content_when_maximized", async () => {
      mockContentPanel.drawerMode.value = "maximized";

      const wrapper = mount(ChangesDrawer, {
        props: { sessionId: "session-1" },
        global: {
          provide: {
            sharedDiffs: mockDiffs,
          },
        },
      });
      await flushPromises();

      expect(wrapper.find("#changes-drawer-content").exists()).toBe(true);
    });
  });
});
