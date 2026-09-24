import { flushPromises, mount } from "@vue/test-utils";
import { defineComponent, h } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";

const { apiFetchMock } = vi.hoisted(() => ({ apiFetchMock: vi.fn() }));
vi.mock("@/lib/api-client", () => ({ apiFetch: apiFetchMock }));
vi.mock("@/composables/use-signalr-socket", () => ({ onGlobalEvent: () => () => {} }));
vi.mock("@/composables/use-enabled-harnesses", async () => {
  const { ref } = await import("vue");
  const harnesses = [
    { type: "opencode", displayName: "OpenCode", capabilities: { supportsWorkflowSteps: true } },
    { type: "opencode2", displayName: "OpenCode 2", capabilities: { supportsWorkflowSteps: true } },
    { type: "claude-code", displayName: "Claude Code", capabilities: { supportsWorkflowSteps: false } },
  ];
  return { useEnabledHarnesses: () => ({ enabledHarnesses: ref(harnesses), defaultHarnessType: ref("opencode") }) };
});
vi.mock("@/composables/use-model-roles", () => ({
  useModelRoles: () => ({ choiceFor: (harness: string) => ({ model: harness === "opencode2" ? "anthropic/claude-sonnet-5" : "github-copilot/gpt-5.4", effort: null }) }),
}));
vi.mock("@/components/ui/dialog", () => {
  const pass = (name: string) => defineComponent({ name, setup: (_p, { slots }) => () => h("div", slots.default?.()) });
  return {
    Dialog: defineComponent({ name: "DialogStub", props: { open: Boolean }, setup: (props, { slots }) => () => (props.open ? h("div", slots.default?.()) : null) }),
    DialogContent: pass("DialogContent"),
    DialogDescription: pass("DialogDescription"),
    DialogFooter: pass("DialogFooter"),
    DialogHeader: pass("DialogHeader"),
    DialogTitle: pass("DialogTitle"),
  };
});

import WorkflowCreateDialog from "@/components/workflows/WorkflowCreateDialog.vue";
import { checkOf, fileOf, respond, reviewDraft } from "./workflow-draft-fixtures";

function dialog(source: { id: string; name: string } | null = null) {
  return mount(WorkflowCreateDialog, {
    props: { open: false, repository: "/work/weave-fleet", repositoryName: "weave-fleet", source },
    attachTo: document.body,
  });
}

describe("WorkflowCreateDialog", () => {
  beforeEach(() => apiFetchMock.mockReset());

  it("New creates the file in the repo and opens it", async () => {
    const created = fileOf(checkOf(reviewDraft()), { workflowId: "repo:tidy-up-a-flaky-test" });
    apiFetchMock.mockImplementation(() => respond(created));
    const wrapper = dialog();
    await wrapper.setProps({ open: true });

    expect(wrapper.text()).toContain("New workflow");
    await wrapper.get("[data-testid='workflow-create-name']").setValue("Tidy up a flaky test");
    expect(wrapper.get("[data-testid='workflow-create-file']").text()).toBe("weave-fleet/.weave/workflows/tidy-up-a-flaky-test.yaml");
    await wrapper.get("form").trigger("submit");
    await flushPromises();

    const [path, init] = apiFetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe("/api/workflows/files");
    expect(JSON.parse(init.body as string)).toEqual({ directory: "/work/weave-fleet", name: "Tidy up a flaky test", workflowId: null });
    expect(wrapper.emitted("created")![0]).toEqual([created]);
    expect(wrapper.emitted("update:open")!.at(-1)).toEqual([false]);
  });

  it("Duplicate copies the built-in under a new name", async () => {
    apiFetchMock.mockImplementation(() => respond(fileOf(checkOf(reviewDraft()))));
    const wrapper = dialog({ id: "builtin:build-a-feature", name: "Build a feature" });
    await wrapper.setProps({ open: true });

    expect(wrapper.text()).toContain("Duplicate Build a feature");
    expect((wrapper.get("[data-testid='workflow-create-name']").element as HTMLInputElement).value).toBe("Build a feature, our way");
    await wrapper.get("form").trigger("submit");
    await flushPromises();

    const [, init] = apiFetchMock.mock.calls[0] as [string, RequestInit];
    expect(JSON.parse(init.body as string)).toEqual({ directory: "/work/weave-fleet", name: "Build a feature, our way", workflowId: "builtin:build-a-feature" });
  });

  it("says so when the name is taken, and stays open", async () => {
    apiFetchMock.mockImplementation(() => respond({ error: "There's already a workflow called Build a feature. Pick another name." }, 409));
    const wrapper = dialog();
    await wrapper.setProps({ open: true });

    await wrapper.get("[data-testid='workflow-create-name']").setValue("Build a feature");
    await wrapper.get("form").trigger("submit");
    await flushPromises();

    expect(wrapper.get("[data-testid='workflow-create-error']").text()).toBe("There's already a workflow called Build a feature. Pick another name.");
    expect(wrapper.emitted("created")).toBeUndefined();
  });

  it("Describe it asks for what the workflow should do, says what it costs, and hands it on to be drafted", async () => {
    const wrapper = dialog();
    await wrapper.setProps({ open: true });

    await wrapper.get("[data-testid='workflow-create-describe']").trigger("click");
    const submit = wrapper.get("[data-testid='workflow-describe-submit']");
    expect(wrapper.text()).toContain("What should it do?");
    expect(wrapper.get("[data-testid='workflow-describe-cost']").text())
      .toBe("Asks the model once. From a session it reads the conversation from the cache, so it's cheap.");
    expect(submit.attributes("disabled")).toBeDefined();
    // Only the harnesses workflows run on, on their Standard model.
    const options = wrapper.findAll("[data-testid='workflow-describe-harness'] option").map((o) => o.text());
    expect(options).toEqual(["OpenCode", "OpenCode 2"]);
    expect(wrapper.text()).toContain("On your Standard model: gpt-5.4.");

    await wrapper.get("[data-testid='workflow-describe-harness']").setValue("opencode2");
    await wrapper.get("[data-testid='workflow-describe-text']").setValue("  Bump the dependencies and check nothing broke.  ");
    await wrapper.get("[data-testid='workflow-describe-form']").trigger("submit");

    expect(wrapper.emitted("describe")![0]).toEqual([{ description: "Bump the dependencies and check nothing broke.", harnessType: "opencode2" }]);
    expect(wrapper.emitted("update:open")!.at(-1)).toEqual([false]);
    // Nothing is created: the draft opens unsaved.
    expect(apiFetchMock).not.toHaveBeenCalled();
  });

  it("Duplicate has no Describe it", async () => {
    const wrapper = dialog({ id: "builtin:build-a-feature", name: "Build a feature" });
    await wrapper.setProps({ open: true });

    expect(wrapper.find("[data-testid='workflow-create-describe']").exists()).toBe(false);
  });
});
