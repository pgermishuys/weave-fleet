import { flushPromises, mount } from "@vue/test-utils";
import { defineComponent, h, ref, type DefineComponent } from "vue";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import FileCanvasComponent from "@/components/canvas/FileCanvas.vue";
import type { FileDiffItem } from "@/api/client";
import { useDraftState } from "@/composables/use-draft-state";
import { useCanvasesStore, type FileView } from "@/stores/canvases";
import { useFileBuffersStore } from "@/stores/file-buffers";

const FileCanvas = FileCanvasComponent as unknown as DefineComponent<{ sessionId: string; path: string; view: FileView }>;

const { readSessionFileMock, writeSessionFileMock } = vi.hoisted(() => ({
  readSessionFileMock: vi.fn(),
  writeSessionFileMock: vi.fn(),
}));

vi.mock("@/api/session-files", () => ({
  readSessionFile: readSessionFileMock,
  writeSessionFile: writeSessionFileMock,
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

const sharedDiffs = { diffs: ref<FileDiffItem[]>([]) };

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

describe("FileCanvas", () => {
  // The test setup gives each test a fresh pinia, shared with mounted components.
  beforeEach(() => {
    localStorage.clear();
    readSessionFileMock.mockReset();
    writeSessionFileMock.mockReset();
    sharedDiffs.diffs.value = [];
  });

  afterEach(() => {
    document.body.innerHTML = "";
  });

  it("opens the file in the editor, clean", async () => {
    readSessionFileMock.mockResolvedValue(file("const one = 1;\n"));
    const wrapper = await mountCanvas();

    expect(readSessionFileMock).toHaveBeenCalledWith("s1", "src/app.ts");
    expect(editor().state.doc.toString()).toBe("const one = 1;\n");
    expect(wrapper.find('[data-testid="file-state"]').exists()).toBe(false);
    expect(wrapper.get(".file-canvas__crumb-file").text()).toBe("app.ts");
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

    expect(writeSessionFileMock).toHaveBeenCalledWith("s1", "src/app.ts", "// hi\nconst one = 1;\n", "h1");
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

      expect(writeSessionFileMock).toHaveBeenLastCalledWith("s1", "src/app.ts", "mine base\n", "h9");
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
    readSessionFileMock.mockResolvedValue({ path: "logo.png", content: null, hash: "h", isBinary: true, isTruncated: false });
    const wrapper = await mountCanvas("logo.png");
    expect(wrapper.get('[data-testid="file-unavailable"]').text()).toContain("binary or not UTF-8");
    wrapper.unmount();
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
    const focus = vi.fn();
    window.addEventListener("weave:command-focus-prompt", focus);
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
    window.removeEventListener("weave:command-focus-prompt", focus);
    wrapper.unmount();
  });
});
