import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { computed, defineComponent, h, ref } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import ChangesReview from "@/components/canvas/ChangesReview.vue";
import type { FileDiffItem } from "@/api/client";
import { useCanvasesStore } from "@/stores/canvases";

const { readSessionFileMock, writeSessionFileMock, restoreSessionFileMock, fetchMock } = vi.hoisted(() => ({
  readSessionFileMock: vi.fn(),
  restoreSessionFileMock: vi.fn(),
  writeSessionFileMock: vi.fn(),
  fetchMock: vi.fn(),
}));

vi.mock("@/api/session-files", () => ({
  readSessionFile: readSessionFileMock,
  writeSessionFile: writeSessionFileMock,
  restoreSessionFile: restoreSessionFileMock,
}));
vi.mock("@/lib/api-client", () => ({ apiFetchOn: fetchMock }));

const BEFORE = ["export interface Note {", "  id: string;", "  title: string;", "}", "", "const a = 1;", "const b = 2;", "const c = 3;", "const d = 4;", "const e = 5;", "const f = oldName(1);", ""].join("\n");
const AFTER = BEFORE.replace("  title: string;", "  title: string;\n  tags: string[];").replace("oldName", "newName");

const summary = [
  { file: "src/notes.ts", status: "modified", additions: 2, deletions: 1 },
  { file: "README.md", status: "modified", additions: 1, deletions: 0 },
] as FileDiffItem[];

const sharedDiffs = {
  hasLoaded: ref(true),
  base: ref(null),
  diffs: ref<FileDiffItem[]>(summary),
  fetchDiffs: vi.fn(),
  byFile: computed(() => new Map<string, FileDiffItem>()),
};

const extraItems: Record<string, FileDiffItem> = {};

function respond(path: string): Response {
  if (extraItems[path]) return new Response(JSON.stringify(extraItems[path]), { status: 200 });
  const item = path === "README.md"
    ? { file: path, status: "modified", additions: 1, deletions: 0, before: "# Hi\n", after: "# Hi\n\nmore\n" }
    : { file: path, status: "modified", additions: 2, deletions: 1, before: BEFORE, after: AFTER };
  return new Response(JSON.stringify(item), { status: 200 });
}

let pinia = createPinia();

function mountReview() {
  return mount(ChangesReview, {
    props: { sessionId: "s1" },
    global: { plugins: [pinia], provide: { sharedDiffs }, stubs: dialogStubs },
  });
}

const pass = (name: string) => defineComponent({ name, setup: (_p, { slots }) => () => h("div", slots.default?.()) });
const dialogStubs = {
  AlertDialog: defineComponent({ name: "AlertDialogStub", props: { open: Boolean }, setup: (props, { slots }) => () => (props.open ? h("div", slots.default?.()) : null) }),
  AlertDialogContent: pass("AlertDialogContent"),
  AlertDialogDescription: pass("AlertDialogDescription"),
  AlertDialogFooter: pass("AlertDialogFooter"),
  AlertDialogHeader: pass("AlertDialogHeader"),
  AlertDialogTitle: pass("AlertDialogTitle"),
  AlertDialogCancel: defineComponent({ name: "AlertDialogCancel", setup: (_p, { slots }) => () => h("button", slots.default?.()) }),
  AlertDialogAction: defineComponent({ name: "AlertDialogAction", setup: (_p, { slots, attrs }) => () => h("button", attrs, slots.default?.()) }),
};

function confirm(wrapper: ReturnType<typeof mountReview>): Promise<void> {
  return wrapper.get("[data-testid='review-revert-confirm']").trigger("click");
}

function notesSection(wrapper: ReturnType<typeof mountReview>) {
  return wrapper.findAll("[data-testid='review-file']").find((section) => section.attributes("data-review-file") === "src/notes.ts")!;
}

describe("ChangesReview", () => {
  beforeEach(() => {
    pinia = createPinia();
    setActivePinia(pinia);
    localStorage.clear();
    sharedDiffs.diffs.value = summary;
    readSessionFileMock.mockReset();
    writeSessionFileMock.mockReset();
    restoreSessionFileMock.mockReset();
    sharedDiffs.fetchDiffs.mockReset();
    fetchMock.mockReset();
    fetchMock.mockImplementation(async (_machine: unknown, url: string) =>
      respond(decodeURIComponent(new URL(url, "http://x").searchParams.get("path") ?? "")));
  });

  it("shows one section per file with totals, hunks, and folded unchanged runs", async () => {
    const wrapper = mountReview();
    await flushPromises();

    expect(wrapper.findAll("[data-testid='review-file']")).toHaveLength(2);
    expect(wrapper.get("[data-testid='review-totals']").text()).toBe("+3−1");
    expect(wrapper.text()).toContain("export interface Note {");
    expect(wrapper.find("[data-testid='review-revert-hunk']").exists()).toBe(true);
    expect(wrapper.find("[data-testid='review-fold']").exists()).toBe(true);
  });

  it("marks only the changed word on a changed line", async () => {
    const wrapper = mountReview();
    await flushPromises();

    expect(wrapper.findAll("mark").map((mark) => mark.text())).toEqual(["oldName", "newName"]);
  });

  it("opens a folded run of unchanged lines on click", async () => {
    const wrapper = mountReview();
    await flushPromises();
    const before = wrapper.findAll(".rline").length;

    await wrapper.get("[data-testid='review-fold']").trigger("click");

    expect(wrapper.findAll(".rline").length).toBeGreaterThan(before);
  });

  it("collapses a file section when its header is clicked", async () => {
    const wrapper = mountReview();
    await flushPromises();

    await wrapper.findAll(".rfile__head")[1]!.trigger("click");

    expect(wrapper.findAll(".rfile")[1]!.classes()).toContain("rfile--collapsed");
    expect(wrapper.findAll(".rfile")[1]!.find(".rfile__body").exists()).toBe(false);
  });

  it("draws split as old and new columns and remembers the pick", async () => {
    const wrapper = mountReview();
    await flushPromises();
    expect(wrapper.find(".rsplit").exists()).toBe(false);

    await wrapper.get("[data-testid='review-split']").trigger("click");

    expect(wrapper.find(".rsplit").exists()).toBe(true);
    expect(localStorage.getItem("weave:changes-layout")).toBe("split");
  });

  it("wraps long lines only when asked", async () => {
    const wrapper = mountReview();
    await flushPromises();
    expect(wrapper.get(".review").classes()).not.toContain("review--wrap");

    await wrapper.get("[data-testid='review-wrap']").trigger("click");

    expect(wrapper.get(".review").classes()).toContain("review--wrap");
  });

  it("always shows the whole session, with no scope to pick", async () => {
    const wrapper = mountReview();
    await flushPromises();

    expect(wrapper.get("[data-testid='review-scope']").text()).toBe("Whole session");
    expect(wrapper.text()).not.toContain("This turn");
    expect(wrapper.findAll("[data-testid='review-file']").length).toBeGreaterThan(1);
  });

  it("opens a changed file in a file tab on Diff from its header", async () => {
    const wrapper = mountReview();
    await flushPromises();

    await notesSection(wrapper).get("[data-testid='review-open-file']").trigger("click");

    const state = useCanvasesStore().sessionCanvases("s1");
    const tab = state.canvases.find((canvas) => canvas.kind === "file");
    expect(tab?.file?.path).toBe("src/notes.ts");
    expect(tab?.file?.view).toBe("diff");
    expect(state.activeId).toBe(tab?.id);
  });

  it("offers Rendered for a markdown file only", async () => {
    const wrapper = mountReview();
    await flushPromises();

    expect(wrapper.findAll("[data-testid='review-rendered']")).toHaveLength(1);
  });

  it("asks before reverting a hunk, then writes the file with that hunk undone", async () => {
    readSessionFileMock.mockResolvedValue({ path: "src/notes.ts", content: AFTER, hash: "h1" });
    writeSessionFileMock.mockResolvedValue({ saved: true, hash: "h2" });
    const wrapper = mountReview();
    await flushPromises();

    await notesSection(wrapper).get("[data-testid='review-revert-hunk']").trigger("click");
    expect(writeSessionFileMock).not.toHaveBeenCalled();
    await flushPromises();
    await confirm(wrapper);
    await flushPromises();

    expect(writeSessionFileMock).toHaveBeenCalledTimes(1);
    const [, , path, text, hash] = writeSessionFileMock.mock.calls[0]!;
    expect(path).toBe("src/notes.ts");
    expect(hash).toBe("h1");
    expect(text).toContain("  title: string;\n}");
    expect(text).not.toContain("tags");
    expect(sharedDiffs.fetchDiffs).toHaveBeenCalled();
  });

  it("refuses to revert from a stale picture of the file", async () => {
    readSessionFileMock.mockResolvedValue({ path: "src/notes.ts", content: "something newer", hash: "h1" });
    const wrapper = mountReview();
    await flushPromises();

    await notesSection(wrapper).get("[data-testid='review-revert-file']").trigger("click");
    await flushPromises();
    await confirm(wrapper);
    await flushPromises();

    expect(writeSessionFileMock).not.toHaveBeenCalled();
    expect(wrapper.get("[data-testid='review-notice']").text()).toContain("changed while you were reviewing");
  });

  describe("reverting files that aren't plain edits", () => {
    function show(...items: FileDiffItem[]) {
      for (const item of items) extraItems[item.file] = item;
      sharedDiffs.diffs.value = items.map(({ file, status, additions, deletions }) => ({ file, status, additions, deletions }) as FileDiffItem);
    }
    function sectionOf(wrapper: ReturnType<typeof mountReview>, path: string) {
      return wrapper.findAll("[data-testid='review-file']").find((section) => section.attributes("data-review-file") === path)!;
    }
    async function revertFile(path: string) {
      const wrapper = mountReview();
      await flushPromises();
      await sectionOf(wrapper, path).get("[data-testid='review-revert-file']").trigger("click");
      await flushPromises();
      await confirm(wrapper);
      await flushPromises();
      return wrapper;
    }

    it("a deleted file is put back from the base, not saved over a hash it doesn't have", async () => {
      show({ file: "package.json", status: "deleted", additions: 0, deletions: 2, before: "{\r\n}\r\n", after: "" } as FileDiffItem);
      restoreSessionFileMock.mockResolvedValue({ saved: true, hash: "h" });
      await revertFile("package.json");
      expect(writeSessionFileMock).not.toHaveBeenCalled();
      expect(restoreSessionFileMock.mock.calls[0]!.slice(2)).toEqual(["package.json", "{\r\n}\r\n", null]);
    });

    it("a new file is deleted, with the hash of what is on disk", async () => {
      show({ file: "new.txt", status: "added", additions: 1, deletions: 0, before: "", after: "hi\n" } as FileDiffItem);
      readSessionFileMock.mockResolvedValue({ path: "new.txt", content: "hi\n", hash: "h9" });
      restoreSessionFileMock.mockResolvedValue({ saved: true, hash: "h9" });
      await revertFile("new.txt");
      expect(restoreSessionFileMock.mock.calls[0]!.slice(2)).toEqual(["new.txt", null, "h9"]);
    });

    it("a new file that changed since is kept and the reader is told", async () => {
      show({ file: "new.txt", status: "added", additions: 1, deletions: 0, before: "", after: "hi\n" } as FileDiffItem);
      readSessionFileMock.mockResolvedValue({ path: "new.txt", content: "hi there\n", hash: "h9" });
      const wrapper = await revertFile("new.txt");
      expect(restoreSessionFileMock).not.toHaveBeenCalled();
      expect(wrapper.get("[data-testid='review-notice']").text()).toContain("changed while you were reviewing");
    });

    it("reverting a hunk in a CRLF file keeps every other line CRLF", async () => {
      const lines = ["a", "b", "c", "d", "e", "f", "g", "h", "i", "j"];
      const before = lines.join("\r\n") + "\r\n";
      const after = lines.map((l) => (l === "b" ? "B" : l)).join("\r\n") + "\r\n";
      show({ file: "crlf.txt", status: "modified", additions: 1, deletions: 1, before, after } as FileDiffItem);
      readSessionFileMock.mockResolvedValue({ path: "crlf.txt", content: after, hash: "h1" });
      writeSessionFileMock.mockResolvedValue({ saved: true, hash: "h2" });
      const wrapper = mountReview();
      await flushPromises();
      await sectionOf(wrapper, "crlf.txt").get("[data-testid='review-revert-hunk']").trigger("click");
      await flushPromises();
      await confirm(wrapper);
      await flushPromises();
      expect(writeSessionFileMock.mock.calls[0]![3]).toBe(before);
    });

    it("a lockfile starts shut, and opens on a click", async () => {
      show(
        { file: "package-lock.json", status: "modified", additions: 3, deletions: 3, before: "a\n", after: "b\n" } as FileDiffItem,
        { file: "src/a.ts", status: "modified", additions: 1, deletions: 1, before: "a\n", after: "b\n" } as FileDiffItem,
      );
      const wrapper = mountReview();
      await flushPromises();
      expect(sectionOf(wrapper, "package-lock.json").classes()).toContain("rfile--collapsed");
      expect(sectionOf(wrapper, "src/a.ts").classes()).not.toContain("rfile--collapsed");
    });

    it("a huge diff draws its first lines and shows the rest on a click", async () => {
      const before = Array.from({ length: 1200 }, (_, i) => `row ${i}`).join("\n") + "\n";
      const after = Array.from({ length: 1200 }, (_, i) => (i % 2 ? `row ${i}!` : `row ${i}`)).join("\n") + "\n";
      show({ file: "data.json", status: "modified", additions: 600, deletions: 600, before, after } as FileDiffItem);
      const wrapper = mountReview();
      await flushPromises();
      expect(sectionOf(wrapper, "data.json").findAll(".rline").length).toBeLessThanOrEqual(700);
      await sectionOf(wrapper, "data.json").get("[data-testid='review-show-all']").trigger("click");
      expect(sectionOf(wrapper, "data.json").findAll(".rline").length).toBeGreaterThan(1500);
    }, 30000);
  });
});
