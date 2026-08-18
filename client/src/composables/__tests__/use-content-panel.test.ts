import { beforeEach, describe, expect, it, vi } from "vitest";
import { ref } from "vue";
import { provideContentPanelContext, useContentPanelContext } from "@/composables/use-content-panel";
import { flushAll, mountComposable } from "./test-utils";

const DRAWER_MODE_KEY = "weave:changes-drawer-mode";

describe("use-content-panel", () => {
  beforeEach(() => {
    // Clear localStorage before each test
    localStorage.clear();
    vi.clearAllMocks();
  });

  it("initializes with default state", async () => {
    const sessionId = ref<string | null>("session-1");

    const { result } = await mountComposable(() => {
      return provideContentPanelContext(sessionId);
    });

    expect(result.activeTab.value).toBe("files");
    expect(result.filesContext.value).toEqual({
      allChangedFilter: "all",
      selectedFilePath: null,
      expandedDirs: expect.any(Set),
      searchQuery: "",
      scrollTop: 0,
    });
    expect(result.filesContext.value.expandedDirs.size).toBe(0);
    expect(result.drawerMode.value).toBe("collapsed");
  });

  it("selectFile switches to preview tab and sets selectedFilePath", async () => {
    const sessionId = ref<string | null>("session-1");

    const { result } = await mountComposable(() => {
      return provideContentPanelContext(sessionId);
    });

    result.selectFile("src/main.ts");
    await flushAll();

    expect(result.activeTab.value).toBe("preview");
    expect(result.filesContext.value.selectedFilePath).toBe("src/main.ts");
  });

  it("switchToFiles changes tab without losing explorer context", async () => {
    const sessionId = ref<string | null>("session-1");

    const { result } = await mountComposable(() => {
      return provideContentPanelContext(sessionId);
    });

    // Set up some explorer context
    result.updateFilesContext({
      allChangedFilter: "changed",
      searchQuery: "test",
      scrollTop: 100,
    });
    result.selectFile("src/main.ts");
    await flushAll();

    expect(result.activeTab.value).toBe("preview");

    // Switch back to files
    result.switchToFiles();
    await flushAll();

    expect(result.activeTab.value).toBe("files");
    expect(result.filesContext.value.allChangedFilter).toBe("changed");
    expect(result.filesContext.value.searchQuery).toBe("test");
    expect(result.filesContext.value.scrollTop).toBe(100);
    expect(result.filesContext.value.selectedFilePath).toBe("src/main.ts");
  });

  it("switchToTab changes the active tab", async () => {
    const sessionId = ref<string | null>("session-1");

    const { result } = await mountComposable(() => {
      return provideContentPanelContext(sessionId);
    });

    result.switchToTab("details");
    await flushAll();

    expect(result.activeTab.value).toBe("details");

    result.switchToTab("preview");
    await flushAll();

    expect(result.activeTab.value).toBe("preview");
  });

  it("updateFilesContext patches explorer state", async () => {
    const sessionId = ref<string | null>("session-1");

    const { result } = await mountComposable(() => {
      return provideContentPanelContext(sessionId);
    });

    const expandedDirs = new Set(["src", "src/components"]);
    result.updateFilesContext({
      allChangedFilter: "changed",
      expandedDirs,
    });
    await flushAll();

    expect(result.filesContext.value.allChangedFilter).toBe("changed");
    expect(result.filesContext.value.expandedDirs).toBe(expandedDirs);
    expect(result.filesContext.value.searchQuery).toBe("");
    expect(result.filesContext.value.scrollTop).toBe(0);
  });

  it("updateFilesContext preserves Set identity when not patched", async () => {
    const sessionId = ref<string | null>("session-1");

    const { result } = await mountComposable(() => {
      return provideContentPanelContext(sessionId);
    });

    const originalSet = result.filesContext.value.expandedDirs;

    result.updateFilesContext({
      searchQuery: "test",
    });
    await flushAll();

    expect(result.filesContext.value.expandedDirs).toBe(originalSet);
  });

  it("session ID change resets transient state", async () => {
    const sessionId = ref<string | null>("session-1");

    const { result } = await mountComposable(() => {
      return provideContentPanelContext(sessionId);
    });

    // Set up some state
    result.updateFilesContext({
      allChangedFilter: "changed",
      selectedFilePath: "src/main.ts",
      expandedDirs: new Set(["src"]),
      searchQuery: "test",
      scrollTop: 100,
    });
    result.setDrawerMode("expanded");
    result.switchToTab("preview");
    await flushAll();

    expect(result.activeTab.value).toBe("preview");
    expect(result.filesContext.value.allChangedFilter).toBe("changed");
    expect(result.drawerMode.value).toBe("expanded");

    // Change session
    sessionId.value = "session-2";
    await flushAll();

    // Transient state should reset
    expect(result.activeTab.value).toBe("files");
    expect(result.filesContext.value).toEqual({
      allChangedFilter: "all",
      selectedFilePath: null,
      expandedDirs: expect.any(Set),
      searchQuery: "",
      scrollTop: 0,
    });
    expect(result.filesContext.value.expandedDirs.size).toBe(0);
    expect(result.drawerMode.value).toBe("collapsed");
  });

  it("session ID change from null to string resets state", async () => {
    const sessionId = ref<string | null>(null);

    const { result } = await mountComposable(() => {
      return provideContentPanelContext(sessionId);
    });

    result.updateFilesContext({
      allChangedFilter: "changed",
      selectedFilePath: "src/main.ts",
    });
    await flushAll();

    sessionId.value = "session-1";
    await flushAll();

    expect(result.filesContext.value.allChangedFilter).toBe("all");
    expect(result.filesContext.value.selectedFilePath).toBeNull();
  });

  it("session ID change to null does not reset state", async () => {
    const sessionId = ref<string | null>("session-1");

    const { result } = await mountComposable(() => {
      return provideContentPanelContext(sessionId);
    });

    result.updateFilesContext({
      allChangedFilter: "changed",
      selectedFilePath: "src/main.ts",
    });
    await flushAll();

    sessionId.value = null;
    await flushAll();

    // State should not reset when going to null
    expect(result.filesContext.value.allChangedFilter).toBe("changed");
    expect(result.filesContext.value.selectedFilePath).toBe("src/main.ts");
  });

  it("drawer mode persists to localStorage", async () => {
    const sessionId = ref<string | null>("session-1");

    const { result } = await mountComposable(() => {
      return provideContentPanelContext(sessionId);
    });

    result.setDrawerMode("expanded");
    await flushAll();

    expect(localStorage.getItem(DRAWER_MODE_KEY)).toBe("expanded");

    result.setDrawerMode("maximized");
    await flushAll();

    expect(localStorage.getItem(DRAWER_MODE_KEY)).toBe("maximized");
  });

  it("drawer mode reads from localStorage on init", async () => {
    localStorage.setItem(DRAWER_MODE_KEY, "maximized");

    const sessionId = ref<string | null>("session-1");

    const { result } = await mountComposable(() => {
      return provideContentPanelContext(sessionId);
    });

    expect(result.drawerMode.value).toBe("maximized");
  });

  it("drawer mode defaults to collapsed for invalid localStorage value", async () => {
    localStorage.setItem(DRAWER_MODE_KEY, "invalid-mode");

    const sessionId = ref<string | null>("session-1");

    const { result } = await mountComposable(() => {
      return provideContentPanelContext(sessionId);
    });

    expect(result.drawerMode.value).toBe("collapsed");
  });

  it("throws error when useContentPanelContext is called without provider", () => {
    expect(() => {
      useContentPanelContext();
    }).toThrow("useContentPanelContext() was called outside a component that provides ContentPanelContext");
  });
});
