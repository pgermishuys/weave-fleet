import { flushPromises, mount } from "@vue/test-utils";
import { defineComponent, h, ref } from "vue";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const { apiFetchMock } = vi.hoisted(() => ({ apiFetchMock: vi.fn() }));
vi.mock("@/lib/api-client", () => ({ apiFetch: apiFetchMock }));
vi.mock("@/composables/use-signalr-socket", () => ({ onGlobalEvent: () => () => {} }));
vi.mock("@/composables/use-built-in-skills", () => ({ useBuiltInSkills: () => ({ skills: ref([{ name: "fleet-code-review", description: "", enabled: true }]) }) }));
vi.mock("@/composables/use-model-roles", () => ({ useModelRoles: () => ({ choiceFor: () => ({ model: "github-copilot/claude-opus-5.5", effort: null }) }) }));
vi.mock("@/composables/use-enabled-harnesses", () => ({ useEnabledHarnesses: () => ({ defaultHarnessType: ref("opencode") }) }));

// reka-ui renders dialogs through a portal; show them in place.
vi.mock("@/components/ui/alert-dialog", () => {
  const pass = (name: string) => defineComponent({ name, setup: (_p, { slots }) => () => h("div", slots.default?.()) });
  return {
    AlertDialog: defineComponent({ name: "AlertDialogStub", props: { open: Boolean }, setup: (props, { slots }) => () => (props.open ? h("div", slots.default?.()) : null) }),
    AlertDialogContent: pass("AlertDialogContent"),
    AlertDialogDescription: pass("AlertDialogDescription"),
    AlertDialogFooter: pass("AlertDialogFooter"),
    AlertDialogHeader: pass("AlertDialogHeader"),
    AlertDialogTitle: pass("AlertDialogTitle"),
  };
});

import WorkflowEditor from "@/components/workflows/WorkflowEditor.vue";
import { CHECK_DELAY_MS, useWorkflowEditor } from "@/composables/use-workflow-editor";
import type { WorkflowDraft } from "@/lib/workflow-draft";
import { useWorkflowsStore } from "@/stores/workflows";
import { buildRun } from "./workflow-fixtures";
import { checkOf, fileOf, respond, reviewDraft } from "./workflow-draft-fixtures";

const REPO = "/work/repo";
const MISSING_MAX = { line: 38, message: "review sends work back to an earlier step, so it needs a max: how many times it may do that in a run.", step: 3 };

interface Call {
  path: string;
  method: string;
  body: Record<string, unknown>;
}

let calls: Call[] = [];
let checks: ((body: Record<string, unknown>) => unknown)[] = [];
let saves: (() => Promise<Response>)[] = [];

function route(path: string, init?: RequestInit): Promise<Response> {
  const body = init?.body ? JSON.parse(init.body as string) as Record<string, unknown> : {};
  calls.push({ path, method: init?.method ?? "GET", body });
  if (path === "/api/workflows/check") return respond((checks.shift() ?? ((b) => checkOf((b.draft as WorkflowDraft) ?? null)))(body));
  if (path === "/api/workflows/files" && init?.method === "PUT") {
    const next = saves.shift();
    if (next) return next();
    return respond(fileOf(checkOf(body.draft as WorkflowDraft ?? reviewDraft(), { text: (body.text as string) ?? "written" }), { hash: "h2" }));
  }
  if (path === "/api/workflows/files/open") return respond(fileOf(checkOf(reviewDraft()), { hash: "h3" }));
  return respond({});
}

async function mountEditor(options: { comments?: boolean } = {}) {
  const editor = useWorkflowEditor();
  editor.adopt(REPO, fileOf(checkOf(reviewDraft(), options.comments
    ? { text: "# Kept by the platform team.\nname: Build it our way\n", comments: [{ line: 1, text: "Kept by the platform team." }, { line: 9, text: "Minor versions only." }] }
    : {})));
  const wrapper = mount(WorkflowEditor, {
    props: { editor },
    attachTo: document.body,
    global: { stubs: { WorkflowFileEditor: true } },
  });
  await flushPromises();
  return { editor, wrapper };
}

async function settle(): Promise<void> {
  await vi.advanceTimersByTimeAsync(CHECK_DELAY_MS + 10);
  await flushPromises();
}

function saveButton(wrapper: Awaited<ReturnType<typeof mountEditor>>["wrapper"]) {
  return wrapper.get("[data-testid='workflow-save']");
}

describe("WorkflowEditor", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    calls = [];
    checks = [];
    saves = [];
    apiFetchMock.mockReset();
    apiFetchMock.mockImplementation(route);
  });

  afterEach(() => {
    vi.useRealTimers();
    document.body.innerHTML = "";
  });

  it("shows the parser's error on the step and under the canvas, and Save waits until it's fixed", async () => {
    const { wrapper } = await mountEditor();
    expect(saveButton(wrapper).attributes("disabled")).toBeDefined();

    await wrapper.get("[data-testid='workflow-node-review']").trigger("click");
    checks.push((body) => checkOf(body.draft as WorkflowDraft, { errors: [MISSING_MAX] }));
    await wrapper.get("[data-testid='workflow-outcome-max']").setValue("");
    await settle();

    const sent = calls.find((call) => call.path === "/api/workflows/check")!;
    expect((sent.body.draft as WorkflowDraft).steps[3].maxLoops).toBeNull();
    expect(wrapper.get("[data-testid='workflow-problems']").text()).toContain(`line 38 · ${MISSING_MAX.message}`);
    expect(wrapper.get("[data-testid='workflow-node-review']").classes()).toContain("wf-node--error");
    expect(wrapper.get("[data-testid='workflow-unsaved']").text()).toBe("Unsaved");
    expect(saveButton(wrapper).attributes("disabled")).toBeDefined();

    await wrapper.get("[data-testid='workflow-outcome-max']").setValue("3");
    await settle();
    expect(wrapper.get("[data-testid='workflow-problems']").text()).toContain("No problems. Checked by the same parser runs use.");
    expect(saveButton(wrapper).attributes("disabled")).toBeUndefined();

    await saveButton(wrapper).trigger("click");
    await flushPromises();
    const saved = calls.find((call) => call.method === "PUT")!;
    expect(saved.body).toMatchObject({ directory: REPO, workflowId: "repo:build-it-our-way", hash: "h1", force: false });
    expect((saved.body.draft as WorkflowDraft).steps[3].maxLoops).toBe(3);
    expect(saved.body.text).toBeUndefined();
    expect(wrapper.get("[data-testid='workflow-notice']").text()).toContain("Saved .weave/workflows/build-it-our-way.yaml. Commit it with your other changes to share it.");
    expect(wrapper.emitted("saved")).toHaveLength(1);
  });

  it("says runs in progress keep the version they started with", async () => {
    useWorkflowsStore().upsert(buildRun({ id: "r1", workflowId: "repo:build-it-our-way", status: "running" }));
    useWorkflowsStore().upsert(buildRun({ id: "r2", workflowId: "repo:build-it-our-way", status: "waiting" }));
    useWorkflowsStore().upsert(buildRun({ id: "r3", workflowId: "repo:build-it-our-way", status: "done" }));
    const { wrapper } = await mountEditor();

    await wrapper.get("[data-testid='workflow-canvas-settings']").trigger("click");
    await wrapper.get("[data-testid='workflow-name']").setValue("Build it, our way");
    await settle();

    expect(wrapper.get("[data-testid='workflow-runs-note']").text()).toBe("2 runs in progress keep the version they started with.");
    await saveButton(wrapper).trigger("click");
    await flushPromises();
    expect(wrapper.get("[data-testid='workflow-notice']").text()).toContain("2 runs in progress keep the version they started with.");
  });

  it("warns about comments, and the first save from the designer asks and lists them", async () => {
    const { wrapper } = await mountEditor({ comments: true });
    expect(wrapper.get("[data-testid='workflow-comments-banner']").text()).toContain("This file has 2 comments.");

    await wrapper.get("[data-testid='workflow-node-plan']").trigger("click");
    await wrapper.get("[data-testid='workflow-step-title']").setValue("Outline");
    await settle();
    await saveButton(wrapper).trigger("click");
    await flushPromises();

    const confirm = wrapper.get("[data-testid='workflow-comments-confirm']");
    expect(confirm.text()).toContain("Save and remove 2 comments?");
    expect(confirm.text()).toContain("# Kept by the platform team.");
    expect(confirm.text()).toContain("# Minor versions only.");
    expect(calls.some((call) => call.method === "PUT")).toBe(false);

    await wrapper.get("[data-testid='workflow-comments-confirm-save']").trigger("click");
    await flushPromises();
    const saved = calls.find((call) => call.method === "PUT")!;
    expect((saved.body.draft as WorkflowDraft).steps[0].title).toBe("Outline");
  });

  it("Edit in File view keeps the comments: the File view saves the text as it is", async () => {
    const { editor, wrapper } = await mountEditor({ comments: true });

    await wrapper.get("[data-testid='workflow-comments-file-view']").trigger("click");
    await flushPromises();
    expect(editor.view.value).toBe("file");
    expect(editor.text.value).toBe("# Kept by the platform team.\nname: Build it our way\n");

    editor.editText("# Kept by the platform team.\nname: Build it, our way\n");
    checks.push((body) => checkOf(reviewDraft(), { text: body.text as string, comments: [{ line: 1, text: "Kept by the platform team." }] }));
    await settle();
    await saveButton(wrapper).trigger("click");
    await flushPromises();

    const saved = calls.find((call) => call.method === "PUT")!;
    expect(saved.body.text).toBe("# Kept by the platform team.\nname: Build it, our way\n");
    expect(saved.body.draft).toBeUndefined();
    expect(wrapper.find("[data-testid='workflow-comments-confirm']").exists()).toBe(false);
  });

  it("Edit in File view from the confirmation goes back to the text with its comments, not the designer's edits", async () => {
    const { editor, wrapper } = await mountEditor({ comments: true });
    await wrapper.get("[data-testid='workflow-node-plan']").trigger("click");
    await wrapper.get("[data-testid='workflow-step-title']").setValue("Outline");
    await settle();
    await saveButton(wrapper).trigger("click");
    await flushPromises();

    checks.push((body) => checkOf(reviewDraft(), { text: body.text as string, comments: [{ line: 1, text: "Kept by the platform team." }, { line: 9, text: "Minor versions only." }] }));
    await wrapper.get("[data-testid='workflow-comments-confirm-file']").trigger("click");
    await flushPromises();

    expect(editor.view.value).toBe("file");
    expect(editor.text.value).toBe("# Kept by the platform team.\nname: Build it our way\n");
    expect(editor.isDirty.value).toBe(false);
    expect(calls.at(-1)).toMatchObject({ path: "/api/workflows/check", body: { text: "# Kept by the platform team.\nname: Build it our way\n" } });
    expect(calls.some((call) => call.method === "PUT")).toBe(false);
  });

  it("stays in the File view when the designer can't show the text", async () => {
    const { editor, wrapper } = await mountEditor();
    await wrapper.get("[data-testid='workflow-view-file']").trigger("click");
    await flushPromises();

    editor.editText("name: [broken\n");
    checks.push(() => checkOf(null, { text: "name: [broken\n", errors: [{ line: 1, message: "This isn't valid YAML: …", step: null }] }));
    await wrapper.get("[data-testid='workflow-view-designer']").trigger("click");
    await settle();

    expect(editor.view.value).toBe("file");
    expect(wrapper.get("[data-testid='workflow-designer-blocked']").text()).toContain("Fix the errors in the File view to use the designer.");
    expect(wrapper.get("[data-testid='workflow-problems']").text()).toContain("line 1 · This isn't valid YAML");
  });

  it("offers Reload or Keep mine when the file changed on disk", async () => {
    const { editor, wrapper } = await mountEditor();
    await wrapper.get("[data-testid='workflow-canvas-settings']").trigger("click");
    await wrapper.get("[data-testid='workflow-name']").setValue("Mine");
    await settle();
    saves.push(() => respond({ error: "This file changed on disk since you opened it." }, 409));

    await saveButton(wrapper).trigger("click");
    await flushPromises();
    expect(wrapper.get("[data-testid='workflow-conflict']").text()).toContain("build-it-our-way.yaml changed on disk");

    await wrapper.get("[data-testid='workflow-conflict-keep']").trigger("click");
    await flushPromises();
    const puts = calls.filter((call) => call.method === "PUT");
    expect(puts).toHaveLength(2);
    expect(puts[1].body).toMatchObject({ force: true, hash: "h1" });
    expect(editor.file.value?.hash).toBe("h2");
    expect(wrapper.find("[data-testid='workflow-conflict']").exists()).toBe(false);
  });

  it("Reload takes the file as it is on disk and drops the edits", async () => {
    const { editor, wrapper } = await mountEditor();
    await wrapper.get("[data-testid='workflow-canvas-settings']").trigger("click");
    await wrapper.get("[data-testid='workflow-name']").setValue("Mine");
    await settle();
    saves.push(() => respond({ error: "changed" }, 409));
    await saveButton(wrapper).trigger("click");
    await flushPromises();

    await wrapper.get("[data-testid='workflow-conflict-reload']").trigger("click");
    await flushPromises();

    expect(calls.at(-1)).toMatchObject({ path: "/api/workflows/files/open", body: { directory: REPO, workflowId: "repo:build-it-our-way" } });
    expect(editor.draft.value?.name).toBe("Build it our way");
    expect(editor.file.value?.hash).toBe("h3");
    expect(editor.isDirty.value).toBe(false);
  });

  it("Try it asks to save first while there are unsaved edits", async () => {
    const { wrapper } = await mountEditor();
    await wrapper.get("[data-testid='workflow-try']").trigger("click");
    expect(wrapper.emitted("tryIt")).toHaveLength(1);

    await wrapper.get("[data-testid='workflow-canvas-settings']").trigger("click");
    await wrapper.get("[data-testid='workflow-name']").setValue("Changed");
    await wrapper.get("[data-testid='workflow-try']").trigger("click");
    expect(wrapper.emitted("tryIt")).toHaveLength(1);
    expect(wrapper.get("[data-testid='workflow-notice']").text()).toBe("Save first: a run uses the file as saved.");
  });

  it("adds an agent step and a You decide step with +, and greys out what arrives later", async () => {
    const { editor, wrapper } = await mountEditor();

    await wrapper.get("[data-testid='workflow-add-4']").trigger("click");
    const menu = wrapper.get(".wf-add-menu");
    expect(menu.text()).toContain("Arrives with Stage 2");
    expect(menu.text()).toContain("Arrives with Stage 3");
    expect(menu.findAll("button[disabled]")).toHaveLength(2);
    await wrapper.get("[data-testid='workflow-add-agent']").trigger("click");
    await wrapper.get("[data-testid='workflow-add-4']").trigger("click");
    await wrapper.get("[data-testid='workflow-add-you']").trigger("click");

    expect(editor.draft.value?.steps.map((step) => step.id)).toEqual(["plan", "ok-plan", "implement", "review", "ask", "step"]);
    expect(editor.draft.value?.steps[4].choices[0].to).toBe("step");
    expect(wrapper.get("[data-testid='workflow-inspector']").text()).toContain("You decide");
  });
});
