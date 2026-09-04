import { describe, expect, it, beforeEach } from "vitest";
import { defineComponent, h, ref, nextTick } from "vue";
import { mount } from "@vue/test-utils";
import {
  provideContentPanelContext,
  useContentPanelContext,
  type ContentPanelContext,
} from "@/composables/use-content-panel";

function mountHarness(sessionId = ref<string | null>("session-1")): ContentPanelContext {
  let captured!: ContentPanelContext;

  const TestChild = defineComponent({
    name: "TestChild",
    setup() {
      captured = useContentPanelContext();
      return () => h("div");
    },
  });

  const TestRoot = defineComponent({
    name: "TestRoot",
    setup() {
      provideContentPanelContext(sessionId);
      return () => h(TestChild);
    },
  });

  mount(TestRoot);
  return captured;
}

const FILES_TREE_WIDTH_KEY = "weave:files-tree-width";

beforeEach(() => {
  localStorage.clear();
});

describe("useContentPanelContext (post-tab redesign)", () => {
  it("initializes with default files context and file view mode", () => {
    const ctx = mountHarness();

    expect(ctx.viewMode.value).toBe("file");
    expect(ctx.filesContext.value).toMatchObject({
      allChangedFilter: "all",
      selectedFilePath: null,
      searchQuery: "",
      scrollTop: 0,
      filesTreeWidth: 260,
    });
    expect(ctx.filesContext.value.expandedDirs).toBeInstanceOf(Set);
  });

  it("selectFile updates selectedFilePath and resets viewMode to file", async () => {
    const ctx = mountHarness();

    ctx.setViewMode("diff");
    expect(ctx.viewMode.value).toBe("diff");

    ctx.selectFile("src/main.ts");
    await nextTick();

    expect(ctx.filesContext.value.selectedFilePath).toBe("src/main.ts");
    expect(ctx.viewMode.value).toBe("file");
  });

  it("setViewMode switches between file and diff", () => {
    const ctx = mountHarness();

    ctx.setViewMode("diff");
    expect(ctx.viewMode.value).toBe("diff");

    ctx.setViewMode("file");
    expect(ctx.viewMode.value).toBe("file");
  });

  it("updateFilesContext patches values while preserving Set identity for expandedDirs", () => {
    const ctx = mountHarness();
    const originalExpanded = ctx.filesContext.value.expandedDirs;

    ctx.updateFilesContext({ scrollTop: 42, searchQuery: "foo" });
    expect(ctx.filesContext.value.scrollTop).toBe(42);
    expect(ctx.filesContext.value.searchQuery).toBe("foo");
    expect(ctx.filesContext.value.expandedDirs).toBe(originalExpanded);
  });

  it("session change resets transient state and restores persisted filesTreeWidth", async () => {
    localStorage.setItem(FILES_TREE_WIDTH_KEY, "320");
    const sessionId = ref<string | null>("session-1");
    const ctx = mountHarness(sessionId);

    ctx.selectFile("some/file.ts");
    ctx.setViewMode("diff");
    ctx.updateFilesContext({ searchQuery: "abc", scrollTop: 99 });
    await nextTick();

    sessionId.value = "session-2";
    await nextTick();

    expect(ctx.filesContext.value.selectedFilePath).toBeNull();
    expect(ctx.filesContext.value.searchQuery).toBe("");
    expect(ctx.filesContext.value.scrollTop).toBe(0);
    expect(ctx.filesContext.value.allChangedFilter).toBe("all");
    expect(ctx.filesContext.value.filesTreeWidth).toBe(320);
    expect(ctx.viewMode.value).toBe("file");
  });

  it("filesTreeWidth changes are persisted to localStorage", async () => {
    const ctx = mountHarness();

    ctx.updateFilesContext({ filesTreeWidth: 400 });
    await nextTick();

    expect(localStorage.getItem(FILES_TREE_WIDTH_KEY)).toBe("400");
  });

  it("filesTreeWidth is read from localStorage on init", () => {
    localStorage.setItem(FILES_TREE_WIDTH_KEY, "375");
    const ctx = mountHarness();

    expect(ctx.filesContext.value.filesTreeWidth).toBe(375);
  });

  it("allChangedFilter can be toggled via updateFilesContext", () => {
    const ctx = mountHarness();

    ctx.updateFilesContext({ allChangedFilter: "changed" });
    expect(ctx.filesContext.value.allChangedFilter).toBe("changed");

    ctx.updateFilesContext({ allChangedFilter: "all" });
    expect(ctx.filesContext.value.allChangedFilter).toBe("all");
  });

  it("throws when used outside a provider", () => {
    const Standalone = defineComponent({
      setup() {
        useContentPanelContext();
        return () => h("div");
      },
    });

    expect(() => mount(Standalone)).toThrow(/outside a component that provides ContentPanelContext/);
  });
});
