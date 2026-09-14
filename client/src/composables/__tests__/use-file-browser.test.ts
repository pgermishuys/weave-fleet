import { beforeEach, describe, expect, it, vi } from "vitest";
import { mount } from "@vue/test-utils";
import { getActivePinia } from "pinia";
import { defineComponent, type Ref, ref, h } from "vue";
import { provideContentPanelContext, type ContentPanelContext } from "@/composables/use-content-panel";
import type { useFileBrowser as UseFileBrowserType } from "@/composables/use-file-browser";
import { useCanvasesStore } from "@/stores/canvases";
import { flushAll } from "./test-utils";

type FileBrowser = ReturnType<typeof UseFileBrowserType>;

/**
 * useContentPanelContext() (used internally by useFileBrowser) is injected by
 * a descendant of the component that calls provideContentPanelContext(); Vue
 * does not resolve provide/inject within the same component instance. Mount a
 * parent (provider) + child (consumer) pair to mirror real app wiring
 * (see SessionsV2RightPanel.vue).
 */
async function mountFileBrowserHarness(sessionId: Ref<string | null>) {
  const { useFileBrowser } = await import("@/composables/use-file-browser");

  let contentPanel!: ContentPanelContext;
  let fileBrowser!: FileBrowser;

  const Child = defineComponent({
    name: "FileBrowserChild",
    setup() {
      fileBrowser = useFileBrowser(sessionId);
      return () => null;
    },
  });

  const Parent = defineComponent({
    name: "FileBrowserParent",
    setup() {
      contentPanel = provideContentPanelContext(sessionId);
      return () => h(Child);
    },
  });

  const wrapper = mount(Parent, {
    global: {
      plugins: getActivePinia() ? [getActivePinia()!] : [],
    },
  });

  await flushAll();

  return { contentPanel, fileBrowser, wrapper };
}

const { browseSessionDirectoryMock, readSessionFileMock, subscribeV2Mock } = vi.hoisted(() => ({
  browseSessionDirectoryMock: vi.fn(),
  readSessionFileMock: vi.fn(),
  subscribeV2Mock: vi.fn(),
}));

vi.mock("@/api/session-files", () => ({
  browseSessionDirectory: browseSessionDirectoryMock,
  readSessionFile: readSessionFileMock,
}));

vi.mock("@/composables/use-weave-socket", () => ({
  useWeaveSocket: () => ({
    subscribeV2: subscribeV2Mock,
  }),
}));

describe("use-file-browser", () => {
  beforeEach(() => {
    browseSessionDirectoryMock.mockReset();
    readSessionFileMock.mockReset();
    subscribeV2Mock.mockReset();

    browseSessionDirectoryMock.mockResolvedValue({ entries: [] });
    subscribeV2Mock.mockImplementation(() => () => {});
  });

  it("selectFile opens the file as a preview tab", async () => {
    const sessionId = ref<string | null>("session-1");
    const { fileBrowser } = await mountFileBrowserHarness(sessionId);

    fileBrowser.selectFile("src/main.ts");

    const tab = useCanvasesStore().sessionCanvases("session-1").canvases.find((canvas) => canvas.file);
    expect(tab?.file).toEqual({ path: "src/main.ts", preview: true, view: "edit" });
    expect(readSessionFileMock).not.toHaveBeenCalled();
  });

  it("selectFile with keep opens a kept tab", async () => {
    const sessionId = ref<string | null>("session-1");
    const { fileBrowser } = await mountFileBrowserHarness(sessionId);

    fileBrowser.selectFile("README.md", { keep: true });

    const tab = useCanvasesStore().sessionCanvases("session-1").canvases.find((canvas) => canvas.file);
    expect(tab?.file).toEqual({ path: "README.md", preview: false, view: "rendered" });
  });
});
