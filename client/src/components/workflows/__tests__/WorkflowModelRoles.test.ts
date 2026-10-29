import { flushPromises, mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import { computed, defineComponent, h, shallowRef } from "vue";

const { putMock, harnessState, modelRolesOpen } = vi.hoisted(() => {
  const openCode = { type: "opencode", displayName: "OpenCode", available: true, userEnabled: true, capabilities: { supportsWorkflowSteps: true } };
  const claudeCode = { type: "claude-code", displayName: "Claude Code", available: true, userEnabled: true, capabilities: { supportsWorkflowSteps: false } };
  return { modelRolesOpen: { value: true }, putMock: vi.fn(), harnessState: { all: [openCode, claudeCode], enabled: [openCode, claudeCode] } };
});
vi.mock("@/api/client", () => ({
  api: {
    GET: vi.fn(() => Promise.resolve({ data: {}, error: undefined })),
    PUT: putMock.mockImplementation(() => Promise.resolve({ data: {}, error: undefined })),
  },
}));
vi.mock("@/composables/use-enabled-harnesses", () => ({
  useEnabledHarnesses: () => ({
    harnesses: computed(() => harnessState.all),
    enabledHarnesses: computed(() => harnessState.enabled),
    defaultHarnessType: computed(() => "opencode"),
  }),
}));
vi.mock("@/composables/use-harness-catalog", () => ({
  useHarnessCatalog: () => ({ catalog: shallowRef(null), models: computed(() => []), isSupported: computed(() => false) }),
}));
vi.mock("@/composables/use-workflows-nav", () => ({
  useWorkflowsNav: () => ({ library: shallowRef(null), repositoryPath: computed(() => null), reload: vi.fn(), modelRolesOpen }),
}));

import WorkflowModelRoles from "@/components/workflows/WorkflowModelRoles.vue";
import { usePreferencesStore } from "@/stores/preferences";

const Inline = defineComponent({ setup: (_, { slots }) => () => h("div", slots.default?.()) });

function mountDialog() {
  return mount(WorkflowModelRoles, {
    attachTo: document.body,
    global: { stubs: { Dialog: Inline, DialogContent: Inline, DialogHeader: Inline, DialogTitle: Inline, DialogDescription: Inline } },
  });
}

describe("WorkflowModelRoles", () => {
  it("shows a role for each kind of work once Workflows is on", async () => {
    const preferences = usePreferencesStore();
    preferences.preferences = { Workflows: "true" };
    preferences.hasFetched = true;
    const wrapper = mountDialog();
    await flushPromises();

    expect(document.querySelector('[data-testid="workflows-roles-card"]')).not.toBeNull();
    for (const role of ["strong", "standard", "fast"])
      expect(document.querySelector(`[data-testid="workflows-role-${role}"]`)).not.toBeNull();
    // Only harnesses that run workflow steps have roles to set.
    expect(document.body.textContent).not.toContain("Claude Code");
    wrapper.unmount();
  });

  it("draws nothing while the dialog is closed", async () => {
    modelRolesOpen.value = false;
    const wrapper = mount(WorkflowModelRoles);
    await flushPromises();
    expect(document.querySelector('[data-testid="workflows-roles-card"]')).toBeNull();
    modelRolesOpen.value = true;
    wrapper.unmount();
  });

  it("names the harnesses that run workflows, from their capabilities, when none of those is on", async () => {
    const [openCode, claudeCode] = harnessState.all;
    const acme = { ...openCode, type: "acme", displayName: "Acme Code" };
    harnessState.all = [openCode, claudeCode, acme];
    harnessState.enabled = [claudeCode];
    try {
      const preferences = usePreferencesStore();
      preferences.preferences = { Workflows: "true" };
      preferences.hasFetched = true;
      const wrapper = mountDialog();
      await flushPromises();

      expect(document.body.textContent).toContain("Workflows run on OpenCode and Acme Code. Turn one of them on in Settings → Harnesses.");
      wrapper.unmount();
    } finally {
      harnessState.all = [openCode, claudeCode];
      harnessState.enabled = [openCode, claudeCode];
    }
  });
});
