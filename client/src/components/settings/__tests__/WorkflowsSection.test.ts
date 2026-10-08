import { flushPromises, mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import { computed, shallowRef } from "vue";

const { putMock, harnessState } = vi.hoisted(() => {
  const openCode = { type: "opencode", displayName: "OpenCode", available: true, userEnabled: true, capabilities: { supportsWorkflowSteps: true } };
  const claudeCode = { type: "claude-code", displayName: "Claude Code", available: true, userEnabled: true, capabilities: { supportsWorkflowSteps: false } };
  return { putMock: vi.fn(), harnessState: { all: [openCode, claudeCode], enabled: [openCode, claudeCode] } };
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
  useWorkflowsNav: () => ({ library: shallowRef(null), repositoryPath: computed(() => null), reload: vi.fn() }),
}));

import WorkflowsSection from "@/components/settings/WorkflowsSection.vue";
import { usePreferencesStore } from "@/stores/preferences";

describe("WorkflowsSection", () => {
  it("is off by default, with no model roles, and the switch turns it on", async () => {
    const wrapper = mount(WorkflowsSection);
    await flushPromises();

    const toggle = wrapper.get('[data-testid="workflows-switch"]');
    expect(toggle.attributes("aria-checked")).toBe("false");
    expect(wrapper.text()).toContain("Experimental");
    expect(wrapper.find('[data-testid="workflows-roles-card"]').exists()).toBe(false);

    await toggle.trigger("click");
    await flushPromises();

    expect(usePreferencesStore().get("Workflows", "false")).toBe("true");
    expect(putMock).toHaveBeenCalledWith("/api/preferences/{key}", expect.objectContaining({ params: { path: { key: "Workflows" } } }));
    expect(wrapper.find('[data-testid="workflows-roles-card"]').exists()).toBe(true);
    for (const role of ["strong", "standard", "fast"])
      expect(wrapper.find(`[data-testid="workflows-role-${role}"]`).exists()).toBe(true);
    // Only harnesses that run workflow steps have roles to set.
    expect(wrapper.text()).not.toContain("Claude Code");
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
      const wrapper = mount(WorkflowsSection);
      await flushPromises();

      expect(wrapper.text()).toContain("Workflows run on OpenCode and Acme Code. Turn one of them on in Settings → Harnesses.");
    } finally {
      harnessState.all = [openCode, claudeCode];
      harnessState.enabled = [openCode, claudeCode];
    }
  });
});
