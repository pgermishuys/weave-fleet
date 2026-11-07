import { mount } from "@vue/test-utils";
import { defineComponent, h, ref, shallowRef } from "vue";
import { describe, expect, it, vi } from "vitest";
import MachineScope from "@/components/layout/MachineScope.vue";
import WorkflowsPane from "@/components/workflows/WorkflowsPane.vue";
import type { MachineTarget } from "@/lib/machine-target";

const { onByKey } = vi.hoisted(() => ({ onByKey: { kestrel: true, falcon: false } as Record<string, boolean> }));

vi.mock("@/composables/use-workflows-feature", () => ({
  useWorkflowsFeature: (target: MachineTarget) => ({ isWorkflowsEnabled: ref(onByKey[target.key]), setWorkflowsEnabled: vi.fn() }),
}));
vi.mock("@/stores/preferences", () => ({ usePreferencesStore: () => ({ hasFetched: true, get: (_k: string, f: string) => f, ensureLoaded: () => undefined }) }));
vi.mock("@/composables/use-workflows-nav", () => ({ useWorkflowsNav: () => ({ reload: vi.fn() }) }));
vi.mock("@/stores/workflows", () => ({ useWorkflowsStore: () => ({ ensureLoaded: vi.fn() }) }));
vi.mock("@/components/workflows/WorkflowDetailPanel.vue", () => ({ default: { template: "<div data-testid=\"workflow-detail\" />" } }));
vi.mock("@/components/workflows/WorkflowModelRoles.vue", () => ({ default: { template: "<div />" } }));

const target = (key: string) => ({ key, connection: null, isLive: key === "kestrel", api: {} }) as unknown as MachineTarget;

describe("the Workflows pane", () => {
  it("says Workflows is off for the machine the view shows, and follows the switch both ways", async () => {
    const chosen = shallowRef(target("kestrel"));
    const Host = defineComponent({
      setup: () => () => h(MachineScope, { key: chosen.value.key, target: chosen.value }, () => h(WorkflowsPane)),
    });
    const wrapper = mount(Host);
    expect(wrapper.find("[data-testid=workflow-detail]").exists()).toBe(true);

    chosen.value = target("falcon");
    await wrapper.vm.$nextTick();
    expect(wrapper.find("[data-testid=feature-off]").exists()).toBe(true);
    expect(wrapper.find("[data-testid=workflow-detail]").exists()).toBe(false);

    chosen.value = target("kestrel");
    await wrapper.vm.$nextTick();
    expect(wrapper.find("[data-testid=feature-off]").exists()).toBe(false);
    expect(wrapper.find("[data-testid=workflow-detail]").exists()).toBe(true);
  });
});
