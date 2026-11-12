import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { computed, defineComponent, h, ref } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import ChangesReview from "@/components/canvas/ChangesReview.vue";
import type { FileDiffItem } from "@/api/client";
import { useDraftState } from "@/composables/use-draft-state";
import { sessionCommands } from "@/lib/session-commands";
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

function mountReview(options: { attachTo?: HTMLElement } = {}) {
  return mount(ChangesReview, {
    props: { sessionId: "s1" },
    global: { plugins: [pinia], provide: { sharedDiffs }, stubs: dialogStubs },
    ...options,
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

    it("a file reverted clean is not asked for again once it has left the diff", async () => {
      show({ file: "new.txt", status: "added", additions: 1, deletions: 0, before: "", after: "hi\n" } as FileDiffItem);
      readSessionFileMock.mockResolvedValue({ path: "new.txt", content: "hi\n", hash: "h9" });
      restoreSessionFileMock.mockResolvedValue({ saved: true, hash: "h9" });
      sharedDiffs.fetchDiffs.mockImplementation(async () => { sharedDiffs.diffs.value = []; });
      const askedFor = () => fetchMock.mock.calls.filter(([, url]) => String(url).includes("new.txt")).length;
      const wrapper = mountReview();
      await flushPromises();
      const before = askedFor();
      expect(before).toBeGreaterThan(0);
      await sectionOf(wrapper, "new.txt").get("[data-testid='review-revert-file']").trigger("click");
      await flushPromises();
      await confirm(wrapper);
      await flushPromises();
      expect(restoreSessionFileMock).toHaveBeenCalled();
      expect(askedFor()).toBe(before);
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

  describe("find in changes", () => {
    const highlights = new Map<string, Range[]>();
    beforeEach(() => {
      highlights.clear();
      (globalThis as { CSS?: unknown }).CSS = {
        escape: (value: string) => value,
        highlights: {
          set: (name: string, value: { ranges: Range[] }) => highlights.set(name, value.ranges),
          delete: (name: string) => highlights.delete(name),
        },
      };
      (globalThis as { Highlight?: unknown }).Highlight = class {
        ranges: Range[];
        constructor(...ranges: Range[]) {
          this.ranges = ranges;
        }
      };
      Range.prototype.getBoundingClientRect ??= () => new DOMRect(0, 0, 0, 0);
      for (const key of Object.keys(extraItems)) delete extraItems[key];
    });

    function show(...items: FileDiffItem[]) {
      for (const item of items) extraItems[item.file] = item;
      sharedDiffs.diffs.value = items.map(({ file, status, additions, deletions }) => ({ file, status, additions, deletions }) as FileDiffItem);
    }
    function sectionOf(wrapper: ReturnType<typeof mountReview>, path: string) {
      return wrapper.findAll("[data-testid='review-file']").find((section) => section.attributes("data-review-file") === path)!;
    }
    async function openFind(wrapper: ReturnType<typeof mountReview>, query: string) {
      const root = wrapper.get("[data-testid='changes-review']");
      await root.trigger("keydown", { key: "f", ctrlKey: true });
      await flushPromises();
      await wrapper.get("[data-testid='changes-find-input']").setValue(query);
      await settle();
    }
    async function settle() {
      await flushPromises();
      await new Promise((resolve) => requestAnimationFrame(() => resolve(null)));
      await flushPromises();
    }
    const count = (wrapper: ReturnType<typeof mountReview>) => wrapper.get("[data-testid='changes-find-count']").text();
    const currentText = () => highlights.get("changes-find-current")?.map((range) => range.toString()) ?? [];

    it("opens on Ctrl+F inside Changes, keeps the browser's find out, and closes on Esc without reaching the app", async () => {
      const wrapper = mount(ChangesReview, {
        props: { sessionId: "s1" },
        global: { plugins: [pinia], provide: { sharedDiffs }, stubs: dialogStubs },
        attachTo: document.body,
      });
      await flushPromises();
      const root = wrapper.get("[data-testid='changes-review']").element as HTMLElement;

      const press = new KeyboardEvent("keydown", { key: "f", ctrlKey: true, bubbles: true, cancelable: true });
      root.dispatchEvent(press);
      await flushPromises();
      expect(press.defaultPrevented).toBe(true);
      expect(document.activeElement).toBe(wrapper.get("[data-testid='changes-find-input']").element);

      const esc = new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true });
      wrapper.get("[data-testid='changes-find-input']").element.dispatchEvent(esc);
      await flushPromises();
      // The app's Esc (stop the agent) skips a key that was already handled.
      expect(esc.defaultPrevented).toBe(true);
      expect(wrapper.find("[data-testid='changes-find']").exists()).toBe(false);
      expect(document.activeElement).toBe(root);
      wrapper.unmount();
    });

    it("counts matches across files, steps with Enter and Shift+Enter, and wraps", async () => {
      const wrapper = mountReview();
      await flushPromises();
      // notes.ts has six const lines and a changed one ("const f = …" before and after).
      await openFind(wrapper, "Const");
      expect(count(wrapper)).toBe("1 of 7");
      expect(currentText()).toEqual(["const"]);

      const field = wrapper.get("[data-testid='changes-find-input']");
      await field.trigger("keydown", { key: "Enter" });
      await settle();
      expect(count(wrapper)).toBe("2 of 7");
      await field.trigger("keydown", { key: "Enter", shiftKey: true });
      await field.trigger("keydown", { key: "Enter", shiftKey: true });
      await settle();
      expect(count(wrapper)).toBe("7 of 7");
      await wrapper.get("[data-testid='changes-find-next']").trigger("click");
      await settle();
      expect(count(wrapper)).toBe("1 of 7");
    });

    it("says No matches, and steps on F3 and Ctrl+G", async () => {
      const wrapper = mountReview();
      await flushPromises();
      await openFind(wrapper, "zzz");
      expect(count(wrapper)).toBe("No matches");

      await wrapper.get("[data-testid='changes-find-input']").setValue("const");
      await settle();
      const root = wrapper.get("[data-testid='changes-review']");
      await root.trigger("keydown", { key: "F3" });
      await root.trigger("keydown", { key: "g", ctrlKey: true });
      await settle();
      expect(count(wrapper)).toBe("3 of 7");
    });

    it("finds a match in a folded unchanged run and opens the run when it steps there", async () => {
      const before = [...Array.from({ length: 30 }, (_, i) => `row ${i}`), ""].join("\n");
      const after = before.replace("row 1\n", "row 1!\n").replace("row 28\n", "row 28!\n");
      show({ file: "rows.txt", status: "modified", additions: 2, deletions: 2, before, after } as FileDiffItem);
      const wrapper = mountReview();
      await flushPromises();
      expect(sectionOf(wrapper, "rows.txt").find("[data-testid='review-fold']").exists()).toBe(true);

      await openFind(wrapper, "row 15");
      expect(count(wrapper)).toBe("1 of 1");
      expect(sectionOf(wrapper, "rows.txt").find("[data-testid='review-fold']").exists()).toBe(false);
      expect(currentText()).toEqual(["row 15"]);
    });

    it("searches a collapsed file, opens it on the match, and says which collapsed files it can't search", async () => {
      show(
        { file: "package-lock.json", status: "modified", additions: 1, deletions: 1, before: "needle\n", after: "other\n" } as FileDiffItem,
        { file: "src/a.ts", status: "modified", additions: 1, deletions: 1, before: "a\n", after: "b\n" } as FileDiffItem,
        { file: "src/b.ts", status: "modified", additions: 1, deletions: 1, before: "x\n", after: "needle\n" } as FileDiffItem,
      );
      const wrapper = mountReview();
      await flushPromises();
      await sectionOf(wrapper, "src/b.ts").get(".rfile__head").trigger("click");
      expect(sectionOf(wrapper, "src/b.ts").classes()).toContain("rfile--collapsed");
      // Earlier tests' panels are still mounted on the same diffs and fetch for them: count only what opening find asks.
      fetchMock.mockClear();

      await openFind(wrapper, "needle");
      expect(count(wrapper)).toBe("1 of 1");
      expect(sectionOf(wrapper, "src/b.ts").classes()).not.toContain("rfile--collapsed");
      expect(wrapper.get("[data-testid='changes-find-unsearched']").text()).toBe("1 collapsed file not searched");
      // The lockfile is still shut and never fetched.
      expect(sectionOf(wrapper, "package-lock.json").classes()).toContain("rfile--collapsed");
      expect(fetchMock.mock.calls.some(([, url]) => String(url).includes("package-lock.json"))).toBe(false);
    });

    it("loads files that start collapsed past the first few when the bar opens", async () => {
      const items = Array.from({ length: 10 }, (_, i) => ({
        file: `f${i}.ts`, status: "modified", additions: 1, deletions: 1, before: "a\n", after: i === 9 ? "late needle\n" : "b\n",
      }) as FileDiffItem);
      show(...items);
      const wrapper = mountReview();
      await flushPromises();
      expect(sectionOf(wrapper, "f9.ts").classes()).toContain("rfile--collapsed");

      await openFind(wrapper, "needle");
      await settle();
      expect(count(wrapper)).toBe("1 of 1");
      expect(sectionOf(wrapper, "f9.ts").classes()).not.toContain("rfile--collapsed");
      expect(wrapper.find("[data-testid='changes-find-unsearched']").exists()).toBe(false);
    });

    it("counts an unchanged line once in split view and marks both of its copies", async () => {
      const wrapper = mountReview();
      await flushPromises();
      await wrapper.get("[data-testid='review-split']").trigger("click");
      await openFind(wrapper, "  id: string");
      expect(count(wrapper)).toBe("1 of 1");
      expect(currentText()).toEqual(["  id: string", "  id: string"]);
      expect(highlights.get("changes-find-hit")).toHaveLength(2);
    });

    it("keeps its marks when the layout switches", async () => {
      const wrapper = mountReview();
      await flushPromises();
      await openFind(wrapper, "newName");
      expect(currentText()).toEqual(["newName"]);
      await wrapper.get("[data-testid='review-split']").trigger("click");
      await settle();
      const [range] = highlights.get("changes-find-current")!;
      expect(wrapper.find(".rsplit").exists()).toBe(true);
      expect(wrapper.element.contains(range!.startContainer)).toBe(true);
    });

    it("shows the diff of a file viewed as Rendered when a match is in it", async () => {
      const wrapper = mountReview();
      await flushPromises();
      await wrapper.get("[data-testid='review-rendered']").trigger("click");
      await openFind(wrapper, "more");
      expect(count(wrapper)).toBe("1 of 1");
      expect(sectionOf(wrapper, "README.md").find(".rline").exists()).toBe(true);
    });
  });

  describe("selecting lines", () => {
    function textNodes(element: Element): Text[] {
      const walker = document.createTreeWalker(element.querySelector(".rline__tx")!, NodeFilter.SHOW_TEXT);
      const nodes: Text[] = [];
      while (walker.nextNode()) nodes.push(walker.currentNode as Text);
      return nodes;
    }

    /** Select from `from` (its text's start, or `fromEnd` its end) to `to`'s end, or `toStart` its start. */
    async function select(from: Element, to: Element, edges: { fromEnd?: boolean; toStart?: boolean } = {}) {
      const start = edges.fromEnd ? textNodes(from).at(-1)! : textNodes(from)[0]!;
      const end = edges.toStart ? textNodes(to)[0]! : textNodes(to).at(-1)!;
      const range = document.createRange();
      range.setStart(start, edges.fromEnd ? start.length : 0);
      range.setEnd(end, edges.toStart ? 0 : end.length);
      const selection = document.getSelection()!;
      selection.removeAllRanges();
      selection.addRange(range);
      document.dispatchEvent(new Event("selectionchange"));
      await flushPromises();
    }

    function line(wrapper: ReturnType<typeof mountReview>, query: string): Element {
      return notesSection(wrapper).get(`.rline${query}`).element;
    }

    let host: HTMLElement;
    beforeEach(() => {
      host = document.body.appendChild(document.createElement("div"));
      useDraftState("s1", { agentId: "a", modelId: "m" }).setText("");
    });

    it("a selection of the file's lines shows the bar; Add to message puts them in the draft as a reference", async () => {
      const writeText = vi.fn().mockResolvedValue(undefined);
      Object.defineProperty(navigator, "clipboard", { value: { writeText }, configurable: true });
      const focus = vi.fn();
      const stop = sessionCommands.contribute("test", [{ sessionId: "s1", handlers: { "focus-prompt": focus } }]);
      const wrapper = mountReview({ attachTo: host });
      await flushPromises();
      expect(wrapper.find("[data-testid='line-bar']").exists()).toBe(false);

      // From "  id: string;" through the added "  tags: string[];"
      await select(line(wrapper, "[data-new='2']"), line(wrapper, "[data-new='4']"));

      expect(wrapper.get("[data-testid='line-bar-file']").text()).toBe("notes.ts");
      expect(wrapper.get("[data-testid='line-bar-label']").text()).toBe("L2–4");
      expect(wrapper.get("[data-testid='line-bar']").text()).toContain("3 lines");
      await wrapper.get("[data-testid='copy-link']").trigger("click");
      expect(writeText).toHaveBeenLastCalledWith("src/notes.ts:2-4");
      await wrapper.get("[data-testid='copy-quote']").trigger("click");
      expect(writeText).toHaveBeenLastCalledWith("src/notes.ts:2-4\n```ts\n  id: string;\n  title: string;\n  tags: string[];\n```\n");

      await wrapper.get("[data-testid='add-to-message']").trigger("click");

      expect(useDraftState("s1", { agentId: "a", modelId: "m" }).draft.text).toBe("@src/notes.ts:2-4 ");
      expect(focus).toHaveBeenCalledTimes(1);
      expect(wrapper.find("[data-testid='line-bar']").exists()).toBe(false);
      stop();
      wrapper.unmount();
    });

    it("removed lines are quoted as they were, with no link", async () => {
      const wrapper = mountReview({ attachTo: host });
      await flushPromises();

      await select(line(wrapper, "[data-diff-type='remove']"), line(wrapper, "[data-diff-type='remove']"));

      expect(wrapper.get("[data-testid='line-bar-label']").text()).toBe("Old L11");
      expect(wrapper.find("[data-testid='copy-link']").exists()).toBe(false);
      await wrapper.get("[data-testid='add-to-message']").trigger("click");
      expect(useDraftState("s1", { agentId: "a", modelId: "m" }).draft.text).toBe(
        "src/notes.ts:11 (before)\n```ts\nconst f = oldName(1);\n```\n",
      );
      wrapper.unmount();
    });

    it("a selection that only touches the edge of a line leaves it out", async () => {
      const wrapper = mountReview({ attachTo: host });
      await flushPromises();

      await select(line(wrapper, "[data-new='1']"), line(wrapper, "[data-new='4']"), { fromEnd: true, toStart: true });

      expect(wrapper.get("[data-testid='line-bar-label']").text()).toBe("L2–3");
      wrapper.unmount();
    });

    it("in Split, a drag down the old column counts old lines only", async () => {
      localStorage.setItem("weave:changes-layout", "split");
      const wrapper = mountReview({ attachTo: host });
      await flushPromises();

      const removed = notesSection(wrapper).findAll(".rline--remove").at(-1)!.element;
      await notesSection(wrapper).get(".rline[data-side='old'][data-old='10']").trigger("pointerdown");
      await select(notesSection(wrapper).get(".rline[data-side='old'][data-old='10']").element, removed);

      expect(wrapper.get("[data-testid='line-bar-label']").text()).toBe("Old L10–11");
      expect(wrapper.get(".review").attributes("data-pick")).toBe("old");

      // A press in the new column next lets the old selection go (it runs through the new column's rows, so the
      // press would drag it), and takes the new side.
      await notesSection(wrapper).get(".rline[data-side='new'][data-new='3']").trigger("pointerdown");
      expect(document.getSelection()!.rangeCount).toBe(0);
      expect(wrapper.get(".review").attributes("data-pick")).toBe("new");
      await select(notesSection(wrapper).get(".rline[data-side='new'][data-new='3']").element, notesSection(wrapper).get(".rline[data-side='new'][data-new='4']").element);
      expect(wrapper.get("[data-testid='line-bar-label']").text()).toBe("L3–4");
      wrapper.unmount();
    });

    it("a selection outside the review shows no bar", async () => {
      const wrapper = mountReview({ attachTo: host });
      await flushPromises();
      const outside = document.body.appendChild(document.createElement("p"));
      outside.innerHTML = '<span class="rline__tx">elsewhere</span>';

      await select(outside, outside);

      expect(wrapper.find("[data-testid='line-bar']").exists()).toBe(false);
      outside.remove();
      wrapper.unmount();
    });

    it("rendered markdown can be asked about, like a file tab's", async () => {
      const wrapper = mountReview();
      await flushPromises();
      const readme = wrapper.findAll("[data-testid='review-file']").find((section) => section.attributes("data-review-file") === "README.md")!;

      await readme.get("[data-testid='review-rendered']").trigger("click");

      expect(readme.getComponent({ name: "MarkdownRenderer" }).props("annotatable")).toBe(true);
      wrapper.unmount();
    });
  });
});
