import { flushPromises, mount } from "@vue/test-utils";
import { ref } from "vue";
import { describe, expect, it, vi } from "vitest";
import WorkflowsPage from "@/components/pages/WorkflowsPage.vue";

const { enabled, preferences, setWorkflowsEnabled, reload, ensureLoaded } = vi.hoisted(() => ({
  enabled: { value: false },
  preferences: { hasFetched: true },
  setWorkflowsEnabled: vi.fn(),
  reload: vi.fn(),
  ensureLoaded: vi.fn(),
}));

vi.mock("@tanstack/vue-router", () => ({
  useRouter: () => ({ navigate: vi.fn() }),
  useSearch: () => ref({}),
}));
vi.mock("@/composables/use-workflows-feature", () => ({
  useWorkflowsFeature: () => ({ isWorkflowsEnabled: ref(enabled.value), setWorkflowsEnabled }),
}));
vi.mock("@/stores/preferences", () => ({ usePreferencesStore: () => preferences }));
vi.mock("@/composables/use-workflows-nav", () => ({ useWorkflowsNav: () => ({ reload, setFolder: vi.fn(), setActiveWorkflow: vi.fn() }) }));
vi.mock("@/stores/workflows", () => ({ useWorkflowsStore: () => ({ ensureLoaded }) }));
vi.mock("@/components/workflows/WorkflowDetailPanel.vue", () => ({
  default: { template: "<div data-testid=\"workflow-detail\" />" },
}));

vi.mock("@/components/workflows/WorkflowModelRoles.vue", () => ({
  default: { template: "<div data-testid=\"workflow-model-roles-dialog\" />" },
}));

describe("the Workflows page", () => {
  it("says Workflows is off and turns it on from the button", async () => {
    enabled.value = false;
    const wrapper = mount(WorkflowsPage);

    expect(wrapper.text()).toContain("Workflows is off");
    expect(wrapper.find("[data-testid=workflow-detail]").exists()).toBe(false);

    await wrapper.find("[data-testid=feature-turn-on]").trigger("click");
    await flushPromises();
    expect(setWorkflowsEnabled).toHaveBeenCalledWith(true);
    expect(reload).toHaveBeenCalled();
    expect(ensureLoaded).toHaveBeenCalled();
  });

  it("says nothing until the switches have loaded, so a page that's on doesn't flash \"is off\"", () => {
    enabled.value = false;
    preferences.hasFetched = false;
    const wrapper = mount(WorkflowsPage);

    expect(wrapper.find("[data-testid=feature-off]").exists()).toBe(false);
    preferences.hasFetched = true;
  });

  it("shows the workflows themselves once they're on", () => {
    enabled.value = true;
    const wrapper = mount(WorkflowsPage);

    expect(wrapper.find("[data-testid=workflow-detail]").exists()).toBe(true);
    // Model roles are on this page, in a dialog opened from the list.
    expect(wrapper.find("[data-testid=workflow-model-roles-dialog]").exists()).toBe(true);
    expect(wrapper.find("[data-testid=feature-off]").exists()).toBe(false);
  });
});
