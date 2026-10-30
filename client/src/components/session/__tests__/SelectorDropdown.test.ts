import { mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import ModelSelector from "@/components/session/ModelSelector.vue";
import { createModelSelectionKey, type ModelOption } from "@/composables/use-models";
import { modelPickerKey, useModelPickerStore } from "@/stores/model-picker";

vi.mock("@/api/client", () => ({ api: { GET: vi.fn(), PUT: vi.fn() } }));

const models: ModelOption[] = ["one", "two", "three"].map((id) => ({
  id, name: id, providerId: "p", selectionKey: createModelSelectionKey("p", id), provider: "P", description: "",
}));

async function open(props: Record<string, unknown> = {}) {
  const wrapper = mount(ModelSelector, { props: { models, modelValue: "", harnessType: "opencode", ...props } });
  await wrapper.get("button").trigger("click");
  return wrapper;
}

describe("selector dropdown", () => {
  it("shows the starred model under Favourites, then All models", async () => {
    const picker = useModelPickerStore();
    vi.spyOn(picker, "toggleFavourite");
    vi.spyOn((await import("@/stores/preferences")).usePreferencesStore(), "get")
      .mockReturnValue(JSON.stringify({ favourites: [modelPickerKey("opencode", "p", "two")] }));
    const wrapper = await open();
    const headings = wrapper.findAll(".selector-dropdown__heading").map((h) => h.text());
    expect(headings).toEqual(["Favourites", "All models"]);
    expect(wrapper.findAll(".selector-dropdown__star--on")).toHaveLength(2);
  });

  it("flattens while typing", async () => {
    const wrapper = await open();
    await wrapper.get("input").setValue("thr");
    expect(wrapper.findAll(".selector-dropdown__heading")).toHaveLength(0);
    // Default stays first while filtering.
    expect(wrapper.findAll(".selector-dropdown__item")).toHaveLength(2);
    // Enter takes the first match, not Default.
    await wrapper.get("input").trigger("keydown", { key: "Enter" });
    expect(wrapper.emitted("update:modelValue")![0]).toEqual([models[2]!.selectionKey]);
  });

  it("picks the active row with the arrows and Enter", async () => {
    const wrapper = await open();
    await wrapper.get("input").trigger("keydown", { key: "ArrowDown" });
    await wrapper.get("input").trigger("keydown", { key: "ArrowDown" });
    await wrapper.get("input").trigger("keydown", { key: "Enter" });
    expect(wrapper.emitted("update:modelValue")![0]).toEqual([models[1]!.selectionKey]);
    expect(wrapper.find(".selector-dropdown").exists()).toBe(false);
  });

  it("stars a model without picking it or closing", async () => {
    const wrapper = await open();
    const picker = useModelPickerStore();
    const toggle = vi.spyOn(picker, "toggleFavourite").mockResolvedValue();
    await wrapper.findAll(".selector-dropdown__star")[0]!.trigger("click");
    expect(toggle).toHaveBeenCalledWith(modelPickerKey("opencode", "p", "one"));
    expect(wrapper.emitted("update:modelValue")).toBeUndefined();
    expect(wrapper.find(".selector-dropdown").exists()).toBe(true);
  });

  it("has no stars or headings without a harness", async () => {
    const wrapper = await open({ harnessType: undefined });
    expect(wrapper.find(".selector-dropdown__star").exists()).toBe(false);
    expect(wrapper.find(".selector-dropdown__heading").exists()).toBe(false);
  });
});

describe("selector dropdown price chip", () => {
  const priced: ModelOption[] = [
    { ...models[0]!, priceTier: "high", capability: "powerful" },
    { ...models[1]!, priceTier: "very_high" },
    { ...models[2]! },
  ];

  it("shows the tier on a model that has one, with its capability as the tooltip", async () => {
    const wrapper = await open({ models: priced });
    const chips = wrapper.findAll('[data-testid="model-price"]');
    expect(chips.map((chip) => chip.text())).toEqual(["High cost", "Very high cost"]);
    expect(chips[0]!.attributes("title")).toBe("Powerful");
    expect(chips[1]!.attributes("title")).toBeUndefined();
  });

  it("puts the chip in the option's name and leaves Default and unpriced models bare", async () => {
    const wrapper = await open({ models: priced });
    const options = wrapper.findAll('[role="option"]');
    expect(options[0]!.find('[data-testid="model-price"]').exists()).toBe(false);
    expect(options[1]!.text()).toContain("High cost");
    expect(options[3]!.find('[data-testid="model-price"]').exists()).toBe(false);
  });

  it("draws no chip for an unknown tier, and the closed picker is unchanged", async () => {
    const wrapper = await open({ models: [{ ...models[0]!, priceTier: "gold" }], modelValue: models[0]!.selectionKey });
    expect(wrapper.find('[data-testid="model-price"]').exists()).toBe(false);
    wrapper.unmount();
    const closed = mount(ModelSelector, { props: { models: priced, modelValue: priced[0]!.selectionKey, harnessType: "opencode" } });
    expect(closed.get("button").text()).toBe("one");
  });
});
