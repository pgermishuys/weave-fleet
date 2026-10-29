import { flushPromises, mount } from "@vue/test-utils";
import { afterEach, describe, expect, it, vi } from "vitest";
import { computed, shallowRef } from "vue";

const modelRolesOpen = await vi.hoisted(async () => (await import("vue")).shallowRef(true));
vi.mock("@/api/client", () => ({
  api: { GET: vi.fn(() => Promise.resolve({ data: {}, error: undefined })), PUT: vi.fn(() => Promise.resolve({ data: {}, error: undefined })) },
}));
vi.mock("@/composables/use-enabled-harnesses", () => {
  const openCode = { type: "opencode", displayName: "OpenCode", available: true, userEnabled: true, capabilities: { supportsWorkflowSteps: true } };
  return {
    useEnabledHarnesses: () => ({
      harnesses: computed(() => [openCode]),
      enabledHarnesses: computed(() => [openCode]),
      defaultHarnessType: computed(() => "opencode"),
    }),
  };
});
vi.mock("@/composables/use-harness-catalog", () => ({
  useHarnessCatalog: () => ({
    catalog: shallowRef(null),
    models: computed(() => [{ id: "m", name: "M", providerId: "p", selectionKey: "p/m", provider: "P", description: "", variants: ["high"] }]),
    isSupported: computed(() => true),
  }),
}));
vi.mock("@/composables/use-workflows-nav", () => ({
  useWorkflowsNav: () => ({ library: shallowRef(null), repositoryPath: computed(() => null), reload: vi.fn(), modelRolesOpen }),
}));

import WorkflowModelRoles from "@/components/workflows/WorkflowModelRoles.vue";
import { usePreferencesStore } from "@/stores/preferences";

// The real reka Dialog, not a stub: its Escape handling is what the menu has to get along with.
async function mountOpen() {
  const preferences = usePreferencesStore();
  preferences.preferences = { Workflows: "true" };
  preferences.hasFetched = true;
  modelRolesOpen.value = true;
  const wrapper = mount(WorkflowModelRoles, { attachTo: document.body, global: { stubs: { teleport: false } } });
  await flushPromises();
  return wrapper;
}

function press(target: Element): void {
  target.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true }));
}

afterEach(() => {
  document.body.innerHTML = "";
});

describe("WorkflowModelRoles Escape", () => {
  it("closes only the open model menu on Escape from its filter, then the dialog on the next", async () => {
    const wrapper = await mountOpen();
    const trigger = document.querySelector<HTMLElement>('[data-testid="workflows-role-model-strong"]')!;
    trigger.click();
    await flushPromises();
    const filter = document.querySelector<HTMLInputElement>(".selector-dropdown__filter")!;
    expect(document.activeElement).toBe(filter);

    press(filter);
    await flushPromises();
    expect(document.querySelector(".selector-dropdown")).toBeNull();
    expect(modelRolesOpen.value).toBe(true);

    press(document.activeElement ?? document.body);
    await flushPromises();
    expect(modelRolesOpen.value).toBe(false);
    wrapper.unmount();
  });

  it("closes only the menu on Escape while focus is on the trigger", async () => {
    const wrapper = await mountOpen();
    const trigger = document.querySelector<HTMLElement>('[data-testid="workflows-role-model-strong"]')!;
    trigger.click();
    await flushPromises();
    trigger.focus();

    press(trigger);
    await flushPromises();
    expect(document.querySelector(".selector-dropdown")).toBeNull();
    expect(modelRolesOpen.value).toBe(true);
    wrapper.unmount();
  });

  it("closes the dialog on Escape when no menu is open, even with focus on a selector", async () => {
    const wrapper = await mountOpen();
    // The dialog puts focus on its first control, which is a selector's button.
    document.querySelector<HTMLElement>('[data-testid="workflows-role-model-strong"]')!.focus();
    press(document.activeElement!);
    await flushPromises();
    expect(modelRolesOpen.value).toBe(false);
    wrapper.unmount();
  });
});
