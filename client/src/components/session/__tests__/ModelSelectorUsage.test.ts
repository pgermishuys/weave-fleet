import { createPinia, setActivePinia } from "pinia";
import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { computed } from "vue";
import type { ProviderUsage } from "@/lib/usage-providers";

const { usage } = vi.hoisted(() => ({ usage: { providers: [] as unknown[] } }));
vi.mock("@/composables/use-usage-limits", () => ({
  useUsageLimits: () => ({ providers: computed(() => usage.providers) }),
}));

import ModelSelector from "@/components/session/ModelSelector.vue";
import { createModelSelectionKey, type ModelOption } from "@/composables/use-models";

const model = (providerId: string, provider: string, n: number): ModelOption => ({
  id: `${providerId}-${n}`, name: `${provider} ${n}`, providerId, provider, description: "",
  selectionKey: createModelSelectionKey(providerId, `${providerId}-${n}`),
});
const models = [
  ...[1, 2, 3, 4, 5].map((n) => model("anthropic", "Anthropic", n)),
  ...[1, 2, 3, 4].map((n) => model("google", "Google", n)),
];
const inHours = (hours: number) => Date.now() + hours * 3_600_000;
const claude = (utilization: number, status: "allowed" | "rejected" = "allowed"): ProviderUsage => ({
  provider: "claude", account: null, accountKey: null, name: "Claude", state: "ok", source: null, harnesses: [], readAt: null, credits: null,
  windows: [{ window: "five_hour", utilization, resetsAt: inHours(2), windowSeconds: 18000, status }],
});

async function open() {
  const wrapper = mount(ModelSelector, { props: { models, modelValue: "", harnessType: "opencode" }, attachTo: document.body });
  await wrapper.get("button").trigger("click");
  await flushPromises();
  return wrapper;
}

describe("ModelSelector usage bars", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    document.body.innerHTML = "";
  });

  it("puts the tightest window's bar on a mapped provider's heading and none on others", async () => {
    usage.providers = [claude(0.6)];
    const wrapper = await open();
    const bars = wrapper.findAll("[data-testid=model-provider-usage]");
    expect(bars).toHaveLength(1);
    expect(bars[0]!.attributes("title")).toBe("60% of the limit used");
    expect(wrapper.find("[data-testid=model-provider-resets]").exists()).toBe(false);
    expect(wrapper.findAll(".selector-dropdown__row--dim")).toHaveLength(0);
    wrapper.unmount();
  });

  it("dims a nearly used-up provider's models with its reset time, and they stay pickable", async () => {
    usage.providers = [claude(0.97)];
    const wrapper = await open();
    expect(wrapper.get("[data-testid=model-provider-resets]").text()).toMatch(/^resets \d{2}:\d{2}$/);
    const dimmed = wrapper.findAll(".selector-dropdown__row--dim");
    expect(dimmed).toHaveLength(5);
    const pick = dimmed[0]!.get("button");
    expect(pick.attributes("disabled")).toBeUndefined();
    await pick.trigger("click");
    expect(wrapper.emitted("update:modelValue")?.[0]).toEqual([models[0]!.selectionKey]);
    wrapper.unmount();
  });

  it("dims a rejected provider", async () => {
    usage.providers = [claude(0.2, "rejected")];
    const wrapper = await open();
    expect(wrapper.findAll(".selector-dropdown__row--dim")).toHaveLength(5);
    wrapper.unmount();
  });

  it("shows no bar without readable usage", async () => {
    usage.providers = [];
    const wrapper = await open();
    expect(wrapper.find("[data-testid=model-provider-usage]").exists()).toBe(false);
    wrapper.unmount();
  });
});
