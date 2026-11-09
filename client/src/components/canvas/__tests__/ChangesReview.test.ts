import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { computed, defineComponent, h, ref } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import ChangesReview from "@/components/canvas/ChangesReview.vue";
import type { FileDiffItem } from "@/api/client";
import { useCanvasesStore } from "@/stores/canvases";

const { readSessionFileMock, writeSessionFileMock, fetchMock } = vi.hoisted(() => ({
  readSessionFileMock: vi.fn(),
  writeSessionFileMock: vi.fn(),
  fetchMock: vi.fn(),
}));

vi.mock("@/api/session-files", () => ({
  readSessionFile: readSessionFileMock,
  writeSessionFile: writeSessionFileMock,
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
  diffs: ref<FileDiffItem[]>(summary),
  fetchDiffs: vi.fn(),
  byFile: computed(() => new Map<string, FileDiffItem>()),
};

function respond(path: string): Response {
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

  it("limits 'This turn' to the files the latest turn changed", async () => {
    useCanvasesStore().setTurnFiles("s1", ["README.md"]);
    const wrapper = mountReview();
    await flushPromises();

    expect(wrapper.findAll("[data-testid='review-file']")).toHaveLength(1);
    expect(wrapper.get("[data-testid='review-scope']").text()).toBe("This turn");
    expect(wrapper.get("[data-testid='review-totals']").text()).toBe("+1−0");
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
});
