import { mount } from "@vue/test-utils";
import { readonly, ref, shallowRef } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import FilesChangedView, { type FileDiffViewItem } from "@/components/session/FilesChangedView.vue";
import { SessionDiffsContextKey } from "@/composables/use-session-diffs-context";

function createFile(overrides: Partial<FileDiffViewItem> = {}): FileDiffViewItem {
  return {
    file: "src/App.vue",
    status: "modified",
    additions: 3,
    deletions: 1,
    before: "const value = 1;\n",
    after: "const value = 2;\nexport const extra = true;\n",
    ...overrides,
  };
}

function mountView(props: Partial<InstanceType<typeof FilesChangedView>["$props"]> = {}) {
  return mount(FilesChangedView, {
    attachTo: document.body,
    props,
  });
}

describe("FilesChangedView", () => {
  beforeEach(() => {
    vi.spyOn(window, "requestAnimationFrame").mockImplementation((callback: FrameRequestCallback) => {
      callback(0);
      return 0;
    });
    vi.spyOn(HTMLElement.prototype, "offsetParent", "get").mockReturnValue(document.body);
  });

  it("renders_loading_error_empty_and_unavailable_states", async () => {
    const loadingWrapper = mountView({ isLoading: true, files: [createFile()] });
    const errorWrapper = mountView({ error: "HTTP 500", files: [createFile()] });
    const emptyWrapper = mountView({ files: [] });
    const unavailableWrapper = mountView({ unavailable: true, files: [createFile()] });

    expect(loadingWrapper.get(".files-changed-view__state").attributes("aria-busy")).toBe("true");
    expect(loadingWrapper.text()).toContain("Loading changed files");
    expect(errorWrapper.get(".files-changed-view__state--error").text()).toContain(
      "Changed files could not be loaded.",
    );
    expect(errorWrapper.text()).toContain("HTTP 500");
    expect(emptyWrapper.get(".files-changed-view__state").text()).toContain("No files changed");
    expect(unavailableWrapper.get(".files-changed-view__state").text()).toContain(
      "Diffs unavailable for this session",
    );

    await errorWrapper.get(".files-changed-view__retry-button").trigger("click");

    expect(errorWrapper.emitted("retry")).toEqual([[]]);
  });

  it("uses_diff_context_when_file_props_are_omitted", () => {
    const contextFiles = [createFile({ file: "src/from-context.ts" })];
    const wrapper = mount(FilesChangedView, {
      attachTo: document.body,
      global: {
        provide: {
          [SessionDiffsContextKey as symbol]: {
            diffState: {
              diffs: readonly(ref(contextFiles)),
              available: readonly(shallowRef(true)),
              isLoading: readonly(shallowRef(false)),
              error: readonly(shallowRef<string | undefined>(undefined)),
              fetchDiffs: vi.fn(),
            },
          },
        },
      },
    });

    expect(wrapper.text()).toContain("src/from-context.ts");
    expect(wrapper.get(".files-changed-view__diff-placeholder").text()).toContain("Select a file");
  });

  it("renders_diff_and_updates_selection_when_file_row_is_clicked", async () => {
    const files = [
      createFile({ file: "src/summary-only.ts", before: undefined, after: undefined }),
      createFile({ file: "src/with-content.ts" }),
    ];
    const wrapper = mountView({ files });

    expect(wrapper.find('[data-testid="tool-card-diff"]').exists()).toBe(false);
    expect(wrapper.get(".files-changed-view__diff-placeholder").text()).toContain("Select a file");

    const rows = wrapper.findAll(".files-changed-view__row");
    expect(rows).toHaveLength(2);

    await rows[1]?.trigger("click");

    expect(wrapper.emitted("select")).toEqual([[expect.objectContaining({ file: "src/with-content.ts" })]]);
    expect(wrapper.get(".files-changed-view__selected-path").text()).toBe("src/with-content.ts");
    expect(wrapper.find('[data-testid="tool-card-diff"]').exists()).toBe(true);
    expect(wrapper.findAll('[data-testid="tool-card-diff-row"]').map((row) => row.attributes("data-diff-type")))
      .toEqual(["remove", "add", "add"]);
    expect(wrapper.text()).toContain("export const extra = true;");
    expect(rows[1]?.attributes("aria-selected")).toBe("true");
  });

  it("emits_close_when_back_button_or_escape_is_pressed", async () => {
    const wrapper = mountView({ files: [createFile()] });

    await wrapper.get(".files-changed-view__back-button").trigger("click");
    await wrapper.get(".files-changed-view").trigger("keydown", { key: "Escape" });

    expect(wrapper.emitted("close")).toEqual([[], []]);
  });

  it("supports_arrow_key_navigation_and_focus_wrapping", async () => {
    const files = [
      createFile({ file: "src/first.ts" }),
      createFile({ file: "src/second.ts" }),
    ];
    const wrapper = mountView({ files, selectedFile: files[0] });
    const root = wrapper.get(".files-changed-view");

    await root.trigger("keydown", { key: "ArrowDown" });

    expect(wrapper.emitted("select")?.[0]).toEqual([expect.objectContaining({ file: "src/second.ts" })]);
    expect(document.activeElement).toBe(wrapper.findAll(".files-changed-view__row")[1]?.element);

    await root.trigger("keydown", { key: "ArrowUp" });

    expect(wrapper.emitted("select")?.[1]).toEqual([expect.objectContaining({ file: "src/first.ts" })]);
    expect(document.activeElement).toBe(wrapper.findAll(".files-changed-view__row")[0]?.element);

    const backButton = wrapper.get(".files-changed-view__back-button");
    const rows = wrapper.findAll(".files-changed-view__row");
    (rows[1]?.element as HTMLElement | undefined)?.focus();
    await root.trigger("keydown", { key: "Tab" });
    expect(document.activeElement).toBe(backButton.element);

    (backButton.element as HTMLElement).focus();
    await root.trigger("keydown", { key: "Tab", shiftKey: true });
    expect(document.activeElement).toBe(rows[1]?.element);
  });
});
