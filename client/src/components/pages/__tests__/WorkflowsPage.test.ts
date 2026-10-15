import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { shallowRef } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import WorkflowsPage from "@/components/pages/WorkflowsPage.vue";
import { usePreferencesStore } from "@/stores/preferences";

const mocks = vi.hoisted(() => ({
  navigate: vi.fn(),
  setFolder: vi.fn(),
  setActiveWorkflow: vi.fn(),
}));
const search = shallowRef<{ workflow?: string; folder?: string }>({});

vi.mock("@tanstack/vue-router", () => ({
  useRouter: () => ({ navigate: mocks.navigate }),
  useSearch: () => search,
}));
vi.mock("@/composables/use-workflows-nav", () => ({
  useWorkflowsNav: () => ({ setFolder: mocks.setFolder, setActiveWorkflow: mocks.setActiveWorkflow }),
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

  it("leaves the open workflow and folder alone without a link", async () => {
    mount(WorkflowsPage);
    await flushPromises();

    expect(mocks.setFolder).not.toHaveBeenCalled();
    expect(mocks.setActiveWorkflow).not.toHaveBeenCalled();
    expect(mocks.navigate).not.toHaveBeenCalled();
  });
});
