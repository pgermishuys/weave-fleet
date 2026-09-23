import { flushPromises, mount } from "@vue/test-utils";
import { defineComponent, h } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";

const { apiFetchMock } = vi.hoisted(() => ({ apiFetchMock: vi.fn() }));
vi.mock("@/lib/api-client", () => ({ apiFetch: apiFetchMock }));
vi.mock("@/composables/use-signalr-socket", () => ({ onGlobalEvent: () => () => {} }));
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
});
