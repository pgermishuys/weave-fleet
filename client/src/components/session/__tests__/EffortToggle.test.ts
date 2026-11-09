import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import EffortToggle from "../EffortToggle.vue";

describe("EffortToggle", () => {
  it("cycles the model's levels, then Default, then back", async () => {
    const wrapper = mount(EffortToggle, { props: { modelValue: "low", variants: ["low", "medium", "high", "xhigh"], "onUpdate:modelValue": (v: string) => wrapper.setProps({ modelValue: v }) } });
    const seen: string[] = [];
    for (let i = 0; i < 5; i++) {
      await wrapper.trigger("click");
      seen.push(wrapper.props("modelValue"));
    }
    expect(seen).toEqual(["medium", "high", "xhigh", "", "low"]);
  });

  it("says Default when unset and names the model in the tooltip", () => {
    const unset = mount(EffortToggle, { props: { modelValue: "", variants: ["low", "high"] } });
    expect(unset.text()).toContain("Default");
    const set = mount(EffortToggle, { props: { modelValue: "high", variants: ["low", "high"], rememberedFor: "Opus 5.5", note: "Opus 5.5: back on High" } });
    expect(set.attributes("title")).toBe("High, remembered for Opus 5.5");
    expect(set.text()).toContain("Opus 5.5: back on High");
  });
});
