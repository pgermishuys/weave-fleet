import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { shallowRef } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import WorkflowsPage from "@/components/pages/WorkflowsPage.vue";
import { usePreferencesStore } from "@/stores/preferences";

<<<<<<< HEAD
const mocks = vi.hoisted(() => ({
  navigate: vi.fn(),
  setFolder: vi.fn(),
  setActiveWorkflow: vi.fn(),
=======
const { enabled, preferences, setWorkflowsEnabled, reload, ensureLoaded } = vi.hoisted(() => ({
  enabled: { value: false },
  preferences: { hasFetched: true },
  setWorkflowsEnabled: vi.fn(),
  reload: vi.fn(),
  ensureLoaded: vi.fn(),
>>>>>>> 0ee5b35b (fix(client): don't flash "is off" before preferences have loaded)
}));
const search = shallowRef<{ workflow?: string; folder?: string }>({});

vi.mock("@tanstack/vue-router", () => ({
  useRouter: () => ({ navigate: mocks.navigate }),
  useSearch: () => search,
}));
<<<<<<< HEAD
vi.mock("@/composables/use-workflows-nav", () => ({
  useWorkflowsNav: () => ({ setFolder: mocks.setFolder, setActiveWorkflow: mocks.setActiveWorkflow }),
=======
vi.mock("@/stores/preferences", () => ({ usePreferencesStore: () => preferences }));
vi.mock("@/composables/use-workflows-nav", () => ({ useWorkflowsNav: () => ({ reload }) }));
vi.mock("@/stores/workflows", () => ({ useWorkflowsStore: () => ({ ensureLoaded }) }));
vi.mock("@/components/workflows/WorkflowDetailPanel.vue", () => ({
  default: { template: "<div data-testid=\"workflow-detail\" />" },
>>>>>>> 0ee5b35b (fix(client): don't flash "is off" before preferences have loaded)
}));
vi.mock("@/components/workflows/WorkflowDetailPanel.vue", () => ({ default: { render: () => null } }));

beforeEach(() => {
  setActivePinia(createPinia());
  const preferences = usePreferencesStore();
  preferences.hasFetched = true;
  preferences.preferences = { Workflows: "true" };
  mocks.navigate.mockReset().mockResolvedValue(undefined);
  mocks.setFolder.mockReset();
  mocks.setActiveWorkflow.mockReset();
  search.value = {};
});

describe("WorkflowsPage", () => {
  it("opens the workflow a link names, in its folder, then drops them from the address", async () => {
    search.value = { workflow: "builtin:fix-a-bug", folder: "/home/me/src/rocket" };
    mount(WorkflowsPage);
    await flushPromises();

    expect(mocks.setFolder).toHaveBeenCalledWith({ kind: "repository", path: "/home/me/src/rocket" }, true);
    expect(mocks.setActiveWorkflow).toHaveBeenCalledWith("builtin:fix-a-bug");
    expect(mocks.navigate).toHaveBeenCalledWith({ to: "/workflows", search: {}, replace: true });
  });

<<<<<<< HEAD
  it("leaves the open workflow and folder alone without a link", async () => {
    mount(WorkflowsPage);
    await flushPromises();
=======
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
>>>>>>> 0ee5b35b (fix(client): don't flash "is off" before preferences have loaded)

    expect(mocks.setFolder).not.toHaveBeenCalled();
    expect(mocks.setActiveWorkflow).not.toHaveBeenCalled();
    expect(mocks.navigate).not.toHaveBeenCalled();
  });
});
