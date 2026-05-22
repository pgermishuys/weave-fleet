import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import FilesChangedPanel, { type FileDiffPanelItem } from "@/components/session/FilesChangedPanel.vue";

function createFile(overrides: Partial<FileDiffPanelItem> = {}): FileDiffPanelItem {
  return {
    file: "src/App.vue",
    status: "modified",
    additions: 3,
    deletions: 1,
    ...overrides,
  };
}

describe("FilesChangedPanel", () => {
  it("renders_file_list_stats_and_selected_state", () => {
    const files = [
      createFile({ file: "src/App.vue", additions: 10, deletions: 2 }),
      createFile({ file: "src/new.ts", status: "added", additions: 5, deletions: 0 }),
      createFile({ file: "src/old.ts", status: "deleted", additions: 0, deletions: 7 }),
    ];

    const wrapper = mount(FilesChangedPanel, {
      props: {
        files,
        selectedFile: files[1],
      },
    });

    const rows = wrapper.findAll(".files-changed-panel__row");

    expect(rows).toHaveLength(3);
    expect(wrapper.text()).toContain("src/App.vue");
    expect(wrapper.text()).toContain("src/new.ts");
    expect(wrapper.text()).toContain("src/old.ts");
    expect(wrapper.text()).toContain("+10");
    expect(wrapper.text()).toContain("-7");
    expect(rows[1]?.attributes("aria-current")).toBe("true");
  });

  it("emits_selected_file_when_a_file_row_is_clicked", async () => {
    const files = [
      createFile({ file: "src/App.vue" }),
      createFile({ file: "src/main.ts", additions: 1, deletions: 0 }),
    ];
    const wrapper = mount(FilesChangedPanel, {
      props: {
        files,
        selectedFile: files[0],
      },
    });

    await wrapper.findAll(".files-changed-panel__row")[1]?.trigger("click");

    expect(wrapper.emitted("select")).toEqual([[
      expect.objectContaining({ file: "src/main.ts", additions: 1, deletions: 0 }),
    ]]);
  });

  it("renders_diff_view_when_selected_file_has_before_and_after_content", () => {
    const selectedFile = createFile({
      before: "alpha\nbeta\ngamma",
      after: "alpha\nbeta changed\ngamma\ndelta",
    });

    const wrapper = mount(FilesChangedPanel, {
      props: {
        files: [selectedFile],
        selectedFile,
      },
    });

    const diffRows = wrapper.findAll('[data-testid="tool-card-diff-row"]');

    expect(wrapper.find('[data-testid="tool-card-diff"]').exists()).toBe(true);
    expect(diffRows.map((row) => row.attributes("data-diff-type"))).toEqual([
      "context",
      "remove",
      "add",
      "context",
      "add",
    ]);
    expect(diffRows.map((row) => row.text()).join("\n")).toContain("beta changed");
    expect(diffRows.map((row) => row.text()).join("\n")).toContain("delta");
    expect(wrapper.find(".files-changed-panel__diff-placeholder").exists()).toBe(false);
  });

  it("updates_diff_view_when_switching_between_added_and_deleted_files", async () => {
    const addedFile = createFile({
      file: "src/new.ts",
      status: "added",
      additions: 2,
      deletions: 0,
      before: "",
      after: "export const value = 1;\nexport const enabled = true;",
    });
    const deletedFile = createFile({
      file: "src/old.ts",
      status: "deleted",
      additions: 0,
      deletions: 2,
      before: "const stale = true;\nexport default stale;",
      after: "",
    });

    const wrapper = mount(FilesChangedPanel, {
      props: {
        files: [addedFile, deletedFile],
        selectedFile: addedFile,
      },
    });

    expect(wrapper.find('[data-testid="tool-card-diff"]').exists()).toBe(true);
    expect(wrapper.findAll('[data-testid="tool-card-diff-row"]')
      .map((row) => row.attributes("data-diff-type"))).toEqual(["add", "add"]);
    expect(wrapper.text()).toContain("export const value = 1;");
    expect(wrapper.text()).not.toContain("const stale = true;");

    await wrapper.setProps({ selectedFile: deletedFile });

    expect(wrapper.find('[data-testid="tool-card-diff"]').exists()).toBe(true);
    expect(wrapper.findAll('[data-testid="tool-card-diff-row"]')
      .map((row) => row.attributes("data-diff-type"))).toEqual(["remove", "remove"]);
    expect(wrapper.text()).toContain("const stale = true;");
    expect(wrapper.text()).not.toContain("export const value = 1;");
    expect(wrapper.find(".files-changed-panel__diff-placeholder").exists()).toBe(false);
  });

  it("renders_missing_content_placeholder_for_usediffs_summary_data", () => {
    const useDiffsSummaryFiles: FileDiffPanelItem[] = [
      createFile({ file: "src/App.vue", before: undefined, after: undefined }),
    ];

    const wrapper = mount(FilesChangedPanel, {
      props: {
        files: useDiffsSummaryFiles,
        selectedFile: useDiffsSummaryFiles[0],
      },
    });

    expect(wrapper.find('[data-testid="tool-card-diff"]').exists()).toBe(false);
    expect(wrapper.get(".files-changed-panel__diff-placeholder").text()).toContain(
      "Diff content is unavailable for this file",
    );
    expect(wrapper.text()).toContain("src/App.vue");
    expect(wrapper.text()).toContain("+3");
    expect(wrapper.text()).toContain("-1");
  });

  it("renders_loading_error_empty_and_unavailable_states", () => {
    const loadingWrapper = mount(FilesChangedPanel, {
      props: {
        isLoading: true,
        files: [createFile()],
      },
    });
    const errorWrapper = mount(FilesChangedPanel, {
      props: {
        error: "HTTP 500",
        files: [createFile()],
      },
    });
    const emptyWrapper = mount(FilesChangedPanel, {
      props: {
        files: [],
      },
    });
    const unavailableWrapper = mount(FilesChangedPanel, {
      props: {
        unavailable: true,
        files: [createFile()],
      },
    });

    expect(loadingWrapper.get('[aria-label="Loading changed files"]').attributes("aria-busy")).toBe("true");
    expect(loadingWrapper.find(".files-changed-panel__row").exists()).toBe(false);
    expect(errorWrapper.get(".files-changed-panel__notice--error").text()).toBe("Changed files could not be loaded.");
    expect(emptyWrapper.get(".files-changed-panel__empty").text()).toBe("No changes");
    expect(unavailableWrapper.find(".files-changed-panel").exists()).toBe(false);
  });

  it("normalizes_missing_or_invalid_line_counts", () => {
    const wrapper = mount(FilesChangedPanel, {
      props: {
        files: [
          createFile({ additions: null, deletions: Number.NaN }),
        ],
      },
    });

    expect(wrapper.text()).toContain("+0");
    expect(wrapper.text()).toContain("-0");
  });
});
