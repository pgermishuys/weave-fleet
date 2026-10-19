import { describe, expect, it } from "vitest";
import { mount } from "@vue/test-utils";
import AllowedRulesList from "@/components/session/AllowedRulesList.vue";
import type { AllowedRule } from "@/lib/tools";

const rules: AllowedRule[] = [
  { id: "rule_1", kind: "shell", tool: "bash", patterns: ["bun test*"], createdAt: new Date().toISOString() },
  { id: "rule_2", kind: "shell", tool: "bash", patterns: ["*"], createdAt: new Date().toISOString() },
];

describe("AllowedRulesList", () => {
  it("reads each rule in plain words", () => {
    const wrapper = mount(AllowedRulesList, { props: { rules, level: "ask" } });

    const rows = wrapper.findAll("[data-testid='allowed-rule']");
    expect(rows[0]!.text()).toContain("Run bun test* without asking");
    expect(rows[1]!.text()).toContain("Run any command without asking");
    expect(rows[1]!.classes()).toContain("allowed__row--broad");
  });

  it("removes one rule or all of them", async () => {
    const wrapper = mount(AllowedRulesList, { props: { rules, level: "edits" } });

    await wrapper.findAll("[data-testid='allowed-remove']")[0]!.trigger("click");
    await wrapper.find("[data-testid='allowed-remove-all']").trigger("click");

    expect(wrapper.emitted("remove")).toEqual([["rule_1"]]);
    expect(wrapper.emitted("removeAll")).toHaveLength(1);
  });

  it("greys the rows and says why when the level allows everything", () => {
    const wrapper = mount(AllowedRulesList, { props: { rules, level: "all" } });

    expect(wrapper.find("[data-testid='allowed-dormant']").text()).toContain("this session allows everything");
    expect(wrapper.find(".allowed__rows--dormant").exists()).toBe(true);
  });

  it("says nothing about being dormant when asking is on", () => {
    const wrapper = mount(AllowedRulesList, { props: { rules, level: "ask" } });

    expect(wrapper.find("[data-testid='allowed-dormant']").exists()).toBe(false);
  });
});
