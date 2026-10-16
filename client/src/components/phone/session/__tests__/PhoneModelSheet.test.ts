import { mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import PhoneModelSheet from "@/components/phone/session/PhoneModelSheet.vue";
import { createModelSelectionKey, type ModelOption } from "@/composables/use-models";
import { modelPickerKey, useModelPickerStore } from "@/stores/model-picker";
import { usePreferencesStore } from "@/stores/preferences";

vi.mock("@/api/client", () => ({ api: { GET: vi.fn(), PUT: vi.fn() } }));

const models: ModelOption[] = ["one", "two"].map((id) => ({
  id, name: id, providerId: "p", selectionKey: createModelSelectionKey("p", id), provider: "P", description: "",
}));
const mountSheet = () => mount(PhoneModelSheet, {
  props: { open: true, models, selected: "", harnessType: "opencode" },
  global: { stubs: { BottomSheet: { template: "<div><slot /></div>" } } },
});

describe("PhoneModelSheet", () => {
  it("groups starred models first", () => {
    vi.spyOn(usePreferencesStore(), "get").mockReturnValue(JSON.stringify({ favourites: [modelPickerKey("opencode", "p", "two")] }));
    const wrapper = mountSheet();
    expect(wrapper.findAll(".pms__head").map((h) => h.text())).toEqual(["Favourites", "All models"]);
    expect(wrapper.findAll(".pms__star--on")).toHaveLength(2);
  });

  it("stars without picking or closing", async () => {
    const toggle = vi.spyOn(useModelPickerStore(), "toggleFavourite").mockResolvedValue();
    const wrapper = mountSheet();
    await wrapper.findAll(".pms__star")[0]!.trigger("click");
    expect(toggle).toHaveBeenCalledWith(modelPickerKey("opencode", "p", "one"));
    expect(wrapper.emitted("pick")).toBeUndefined();
    expect(wrapper.emitted("close")).toBeUndefined();
  });
});
