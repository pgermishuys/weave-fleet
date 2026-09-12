import { beforeEach, describe, expect, it, vi } from "vitest";
import { mount } from "@vue/test-utils";
import { getActivePinia } from "pinia";
import { defineComponent, type Ref, ref, h } from "vue";
import { provideContentPanelContext, type ContentPanelContext } from "@/composables/use-content-panel";
import type { useFileBrowser as UseFileBrowserType } from "@/composables/use-file-browser";
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

  it("selectFile reads the file and shows it in the canvas viewer", async () => {
    readSessionFileMock.mockResolvedValue({ content: "hello", isBinary: false });

    const sessionId = ref<string | null>("session-1");
    const { contentPanel, fileBrowser } = await mountFileBrowserHarness(sessionId);

    await fileBrowser.selectFile("src/main.ts");
    await flushAll();

    expect(readSessionFileMock).toHaveBeenCalledWith("session-1", "src/main.ts");
    expect(contentPanel.filePayload.value).toMatchObject({
      sourceFilePath: "src/main.ts",
      sourceText: "hello",
    });
  });

  it("selectFile shows a notice instead of binary content", async () => {
    readSessionFileMock.mockResolvedValue({ content: "", isBinary: true });

    const sessionId = ref<string | null>("session-1");
    const { contentPanel, fileBrowser } = await mountFileBrowserHarness(sessionId);

    await fileBrowser.selectFile("assets/logo.png");
    await flushAll();

    expect(contentPanel.filePayload.value?.$type).toBe("markdown");
    expect(contentPanel.filePayload.value?.content).toContain("Cannot display binary file");
    expect(contentPanel.filePayload.value?.sourceFilePath).toBe("assets/logo.png");
  });
});
