import { flushPromises, mount } from "@vue/test-utils";
import { computed, defineComponent, h, ref, type ComputedRef, type DefineComponent } from "vue";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import FileCanvasComponent from "@/components/canvas/FileCanvas.vue";
import type { FileDiffItem } from "@/api/client";
import { clearDiffBaseCache } from "@/composables/use-diff-base";
import { useDraftState } from "@/composables/use-draft-state";
import { sessionCommands } from "@/lib/session-commands";
import { useCanvasesStore, type FileView } from "@/stores/canvases";
import { useFileBuffersStore } from "@/stores/file-buffers";
import { useGoToFileStore } from "@/stores/go-to-file";

const FileCanvas = FileCanvasComponent as unknown as DefineComponent<{ sessionId: string; path: string; view: FileView }>;

const { readSessionFileMock, writeSessionFileMock } = vi.hoisted(() => ({
  readSessionFileMock: vi.fn(),
  writeSessionFileMock: vi.fn(),
}));

vi.mock("@/api/session-files", () => ({
  readSessionFile: readSessionFileMock,
  writeSessionFile: writeSessionFileMock,
}));

// A changed file's base comes from /diffs/file; answer it from the diff list below. Images come from
// /files/image, answered from `images`.
const images = vi.hoisted(() => new Map<string, Uint8Array<ArrayBuffer> | string>());
vi.mock("@/lib/api-client", () => ({
  apiFetchOn: vi.fn(async (_machine: unknown, url: string) => {
    const path = new URL(url, "http://fleet").searchParams.get("path");
    if (url.includes("/files/image")) {
      const image = path ? images.get(path) : undefined;
      return image ? new Response(image, { status: 200 }) : new Response(null, { status: 404 });
    }
    const item = sharedDiffs.diffs.value.find((diff) => diff.file === path);
    return item ? new Response(JSON.stringify(item), { status: 200 }) : new Response(null, { status: 404 });
  }),
}));

vi.mock("@/components/visual-renderers/MarkdownRenderer.vue", () => ({
  default: defineComponent({
    name: "MarkdownRenderer",
    props: { content: String, annotatable: Boolean },
    setup(props) {
      return () => h("div", { class: "stub-markdown", "data-annotatable": String(props.annotatable) }, props.content);
    },
  }),
}));

const sharedDiffs = vi.hoisted(() => ({}) as {
  diffs: import("vue").Ref<FileDiffItem[]>;
  byFile: ComputedRef<ReadonlyMap<string, FileDiffItem>>;
  base: import("vue").Ref<null>;
});
sharedDiffs.diffs = ref<FileDiffItem[]>([]);
sharedDiffs.base = ref(null);
sharedDiffs.byFile = computed(() => new Map(sharedDiffs.diffs.value.map((diff) => [diff.file, diff])));

function file(content: string, hash = "h1") {
  return { path: "src/app.ts", content, hash, isBinary: false, isTruncated: false };
}

async function mountCanvas(path = "src/app.ts", view: FileView = "edit") {
  useCanvasesStore().openFile("s1", path, { keep: true, view });
  const wrapper = mount(FileCanvas, {
    props: { sessionId: "s1", path, view },
    global: { provide: { sharedDiffs } },
    attachTo: document.body,
  });
  await flushPromises();
  return wrapper;
}

function editor(path = "src/app.ts") {
  const view = useFileBuffersStore().record("s1", path)?.view;
  if (!view) throw new Error("no editor");
  return view;
}

function type(text: string, at = 0, path = "src/app.ts") {
  editor(path).dispatch({ changes: { from: at, insert: text }, userEvent: "input.type" });
}


/** Nothing provides another machine here, so calls go to the live one: home. */
const live = expect.objectContaining({ key: "home", isLive: true });

describe("FileCanvas", () => {
  // The test setup gives each test a fresh pinia, shared with mounted components.
  beforeEach(() => {
    localStorage.clear();
    readSessionFileMock.mockReset();
    writeSessionFileMock.mockReset();
    sharedDiffs.diffs.value = [];
    clearDiffBaseCache();
  });

  afterEach(() => {
    document.body.innerHTML = "";
  });

  it("opens the file in the editor, clean", async () => {
    readSessionFileMock.mockResolvedValue(file("const one = 1;\n"));
    const wrapper = await mountCanvas();

    expect(readSessionFileMock).toHaveBeenCalledWith(live, "s1", "src/app.ts");
    expect(editor().state.doc.toString()).toBe("const one = 1;\n");
    expect(wrapper.find('[data-testid="file-state"]').exists()).toBe(false);
    expect(wrapper.get(".file-canvas__crumb-file").text()).toBe("app.ts");
    wrapper.unmount();
  });

  it("opens at the line a message named, with the cursor there", async () => {
    readSessionFileMock.mockResolvedValue(file("one\ntwo\nthree\nfour\n"));
    useGoToFileStore().focusOnOpen = { sessionId: "s1", path: "src/app.ts", line: 3 };
    const wrapper = await mountCanvas();

    const view = editor();
    expect(view.state.selection.main.head).toBe(view.state.doc.line(3).from);
    expect(useGoToFileStore().focusOnOpen).toBeNull();
    wrapper.unmount();
  });

  it("moves an open file to another line a message named, or its last line when past the end", async () => {
    readSessionFileMock.mockResolvedValue(file("one\ntwo\nthree\nfour"));
    const wrapper = await mountCanvas();
    const view = editor();

    useGoToFileStore().focusOnOpen = { sessionId: "s1", path: "src/app.ts", line: 2 };
    await flushPromises();
    expect(view.state.selection.main.head).toBe(view.state.doc.line(2).from);

    useGoToFileStore().focusOnOpen = { sessionId: "s1", path: "src/app.ts", line: 40 };
    await flushPromises();
    expect(view.state.selection.main.head).toBe(view.state.doc.line(4).from);

    useGoToFileStore().focusOnOpen = { sessionId: "s1", path: "src/other.ts", line: 1 };
    await flushPromises();
    expect(view.state.selection.main.head).toBe(view.state.doc.line(4).from);
    wrapper.unmount();
  });

  it("shows Unsaved after typing, and saves with the hash it read", async () => {
    readSessionFileMock.mockResolvedValue(file("const one = 1;\n"));
    writeSessionFileMock.mockResolvedValue({ saved: true, hash: "h2" });
    const wrapper = await mountCanvas();

    type("// hi\n");
    await flushPromises();
    expect(wrapper.get('[data-testid="file-state"]').text()).toContain("Unsaved");

    await wrapper.get('[data-testid="file-save"]').trigger("click");
    await flushPromises();

    expect(writeSessionFileMock).toHaveBeenCalledWith(live, "s1", "src/app.ts", "// hi\nconst one = 1;\n", "h1");
    expect(wrapper.find('[data-testid="file-state"]').exists()).toBe(false);
    expect(wrapper.get(".file-canvas__toast").text()).toBe("Saved src/app.ts");
    expect(useFileBuffersStore().record("s1", "src/app.ts")?.baseHash).toBe("h2");
    wrapper.unmount();
  });

  it("Ctrl S saves through the editor's keymap", async () => {
    readSessionFileMock.mockResolvedValue(file("a\n"));
    writeSessionFileMock.mockResolvedValue({ saved: true, hash: "h2" });
    const wrapper = await mountCanvas();
    type("b");

    useFileBuffersStore().record("s1", "src/app.ts")?.handlers.save?.();
    await flushPromises();

    expect(writeSessionFileMock).toHaveBeenCalledTimes(1);
    wrapper.unmount();
  });

  it("typing in a preview tab keeps it", async () => {
    readSessionFileMock.mockResolvedValue(file("a\n"));
    const canvases = useCanvasesStore();
    canvases.openFile("s1", "src/app.ts");
    const wrapper = mount(FileCanvas, {
      props: { sessionId: "s1", path: "src/app.ts", view: "edit" },
      global: { provide: { sharedDiffs } },
      attachTo: document.body,
    });
    await flushPromises();

    type("b");

    expect(canvases.sessionCanvases("s1").canvases.find((canvas) => canvas.file)?.file?.preview).toBe(false);
    wrapper.unmount();
  });

  describe("when the save loses the race (409)", () => {
    async function conflicted() {
      readSessionFileMock.mockResolvedValue(file("base\n"));
      writeSessionFileMock.mockResolvedValueOnce({ saved: false, content: "agent's\n", hash: "h9" });
      const wrapper = await mountCanvas();
      type("mine ");
      await flushPromises();
      await wrapper.get('[data-testid="file-save"]').trigger("click");
      await flushPromises();
      return wrapper;
    }

    it("shows the bar and changes nothing", async () => {
      const wrapper = await conflicted();
      expect(wrapper.get('[data-testid="file-conflict"]').text()).toContain("The agent changed app.ts while you were editing.");
      expect(wrapper.get('[data-testid="file-state"]').text()).toContain("Changed on disk");
      expect(editor().state.doc.toString()).toBe("mine base\n");
      wrapper.unmount();
    });

    it("Use the agent's replaces the buffer", async () => {
      const wrapper = await conflicted();
      await wrapper.get('[data-testid="conflict-theirs"]').trigger("click");
      await flushPromises();

      expect(editor().state.doc.toString()).toBe("agent's\n");
      expect(wrapper.find('[data-testid="file-conflict"]').exists()).toBe(false);
      expect(useFileBuffersStore().info("s1", "src/app.ts")?.dirty).toBe(false);
      wrapper.unmount();
    });

    it("Keep mine saves over the agent's version it showed", async () => {
      const wrapper = await conflicted();
      writeSessionFileMock.mockResolvedValueOnce({ saved: true, hash: "h10" });

      await wrapper.get('[data-testid="conflict-mine"]').trigger("click");
      await flushPromises();

      expect(writeSessionFileMock).toHaveBeenLastCalledWith(live, "s1", "src/app.ts", "mine base\n", "h9");
      expect(wrapper.find('[data-testid="file-conflict"]').exists()).toBe(false);
      expect(wrapper.get(".file-canvas__toast").text()).toBe("Saved your version of app.ts");
      wrapper.unmount();
    });

    it("Compare shows the merge view; Done rebases on the agent's version and stays unsaved", async () => {
      const wrapper = await conflicted();
      await wrapper.get('[data-testid="conflict-compare"]').trigger("click");
      await flushPromises();

      expect(wrapper.get('[data-testid="file-comparing"]').text()).toContain("Red is the agent's version");
      expect(wrapper.find(".cm-deletedChunk, .cm-changedLine").exists()).toBe(true);

      await wrapper.get('[data-testid="compare-done"]').trigger("click");
      await flushPromises();

      const record = useFileBuffersStore().record("s1", "src/app.ts");
      expect(record?.baseHash).toBe("h9");
      expect(wrapper.find('[data-testid="file-comparing"]').exists()).toBe(false);
      expect(wrapper.get('[data-testid="file-state"]').text()).toContain("Unsaved");
      expect(wrapper.get(".file-canvas__toast").text()).toBe("Merged. Save to write it.");
      wrapper.unmount();
    });
  });

  it("says why a file too large to edit can't be opened", async () => {
    readSessionFileMock.mockResolvedValue({ path: "big.log", content: null, hash: null, isBinary: false, isTruncated: true });
    const wrapper = await mountCanvas("big.log");
    expect(wrapper.get('[data-testid="file-unavailable"]').text()).toBe("Too large to edit here (over 512 KB).");
    expect(wrapper.find('[data-testid="file-view-edit"]').exists()).toBe(false);
    wrapper.unmount();
  });

  it("says why a binary file can't be opened", async () => {
    readSessionFileMock.mockResolvedValue({ path: "font.woff2", content: null, hash: "h", isBinary: true, isTruncated: false });
    const wrapper = await mountCanvas("font.woff2");
    expect(wrapper.get('[data-testid="file-unavailable"]').text()).toContain("binary or not UTF-8");
    wrapper.unmount();
  });

  describe("images", () => {
    const createObjectURL = URL.createObjectURL;
    const revokeObjectURL = URL.revokeObjectURL;

    beforeEach(() => {
      images.clear();
      URL.createObjectURL = vi.fn(() => "blob:picture");
      URL.revokeObjectURL = vi.fn();
    });

    afterEach(() => {
      URL.createObjectURL = createObjectURL;
      URL.revokeObjectURL = revokeObjectURL;
    });

    it("shows an image as a picture with its pixel size and file size, never in the editor, however large", async () => {
      images.set("docs/map.png", new Uint8Array(600 * 1024));
      const wrapper = await mountCanvas("docs/map.png");

      expect(readSessionFileMock).not.toHaveBeenCalled();
      expect(wrapper.find('[data-testid="file-unavailable"]').exists()).toBe(false);
      expect(wrapper.find('[data-testid="file-view-edit"]').exists()).toBe(false);
      const img = wrapper.get('[data-testid="file-image"] img');
      expect(img.attributes("src")).toBe("blob:picture");
      expect(img.attributes("alt")).toBe("map.png");

      Object.defineProperty(img.element, "naturalWidth", { value: 1440 });
      Object.defineProperty(img.element, "naturalHeight", { value: 900 });
      await img.trigger("load");
      expect(wrapper.get('[data-testid="image-meta"]').text()).toBe("1440 × 900 · 600 KB");
      wrapper.unmount();
    });

    it("shows an SVG as a picture too, with its size alone when it has no pixel size", async () => {
      images.set("logo.svg", "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 4 4'/>");
      const wrapper = await mountCanvas("logo.svg");

      expect(readSessionFileMock).not.toHaveBeenCalled();
      await wrapper.get('[data-testid="file-image"] img').trigger("load");
      expect(wrapper.get('[data-testid="image-meta"]').text()).toBe("59 B");
      wrapper.unmount();
    });

    it("says so when the image is no longer there", async () => {
      const wrapper = await mountCanvas("docs/gone.png");

      expect(wrapper.get('[data-testid="file-unavailable"]').text()).toBe("This file isn't in the session's folder any more.");
      expect(wrapper.find('[data-testid="file-image"]').exists()).toBe(false);
      wrapper.unmount();
    });
  });

  it("Markdown opens rendered from the buffer, annotatable, with Source and Diff beside it", async () => {
    readSessionFileMock.mockResolvedValue({ ...file("# Title\n"), path: "docs/guide.md" });
    const wrapper = await mountCanvas("docs/guide.md", "rendered");

    expect(wrapper.get(".stub-markdown").text()).toBe("# Title");
    expect(wrapper.get(".stub-markdown").attributes("data-annotatable")).toBe("true");
    expect(wrapper.get('[data-testid="file-view-edit"]').text()).toBe("Source");
    expect(wrapper.get('[data-testid="file-view-diff"]').attributes("disabled")).toBeDefined();

    // Unsaved edits show in Rendered too.
    type("Hello ", 2, "docs/guide.md");
    await flushPromises();
    expect(wrapper.get(".stub-markdown").text()).toBe("# Hello Title");

    await wrapper.get('[data-testid="file-view-edit"]').trigger("click");
    await flushPromises();
    expect(useCanvasesStore().sessionCanvases("s1").canvases.find((canvas) => canvas.file)?.file?.view).toBe("edit");
    wrapper.unmount();
  });

  it("Diff shows the git base against the buffer, editable", async () => {
    sharedDiffs.diffs.value = [
      { file: "src/app.ts", status: "modified", additions: 1, deletions: 1, before: "const one = 1;\n", after: "const one = 111;\n" },
    ] as FileDiffItem[];
    readSessionFileMock.mockResolvedValue(file("const one = 111;\n"));
    const wrapper = await mountCanvas("src/app.ts", "diff");

    expect(wrapper.get('[data-testid="file-view-diff"]').attributes("aria-pressed")).toBe("true");
    expect(wrapper.find(".cm-deletedChunk").exists()).toBe(true);
    expect(wrapper.find(".cm-merge-reject").text()).toBe("Revert");
    wrapper.unmount();
  });

  it("Add to message puts a line reference in the draft and focuses the composer", async () => {
    readSessionFileMock.mockResolvedValue(file("one\ntwo\nthree\nfour\n"));
    // The plumbing for hearing the request lives here only, so the expectations don't depend on it.
    const focus = vi.fn();
    const stop = sessionCommands.contribute("test", [{ sessionId: "s1", handlers: { "focus-prompt": focus } }]);
    const wrapper = await mountCanvas();
    const view = editor();
    // jsdom has no layout or contenteditable focus; the chip only needs a place and focus.
    vi.spyOn(view, "hasFocus", "get").mockReturnValue(true);
    vi.spyOn(view, "coordsAtPos").mockReturnValue({ left: 40, right: 40, top: 30, bottom: 44 });

    view.dispatch({ selection: { anchor: view.state.doc.line(2).from, head: view.state.doc.line(4).from } });
    await flushPromises();

    const chip = wrapper.get('[data-testid="add-to-message"]');
    expect(chip.text()).toBe("Add lines 2–3 to message");
    await chip.trigger("click");

    expect(useDraftState("s1", { agentId: "a", modelId: "m" }).draft.text).toBe("@src/app.ts:2-3 ");
    expect(focus).toHaveBeenCalledTimes(1);
    stop();
    wrapper.unmount();
  });
});
