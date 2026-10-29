import { mount } from "@vue/test-utils";
import type { Component } from "vue";
import { describe, expect, it } from "vitest";
import SettingGroupComponent from "@/components/settings/SettingGroup.vue";
import SettingRowComponent from "@/components/settings/SettingRow.vue";
import SettingSwitch from "@/components/settings/SettingSwitch.vue";

// CI's vue-tsc (npm ci on Node 22) finds no mount() overload for these slotted components; the props are checked by the components themselves.
const SettingRow = SettingRowComponent as unknown as Component;
const SettingGroup = SettingGroupComponent as unknown as Component;

describe("SettingRow", () => {
  it("shows the summary, and More opens today's full text word for word", async () => {
    const wrapper = mount(SettingRow, {
      props: { id: "demo", title: "Demo", summary: "One line." },
      slots: { more: "The full text, as it always was.", control: "<span data-testid='ctl'>control</span>" },
    });
    expect(wrapper.text()).toContain("One line.");
    expect(wrapper.find("[data-testid='setting-more-text-demo']").exists()).toBe(false);
    expect(wrapper.find("[data-testid='ctl']").exists()).toBe(true);

    const more = wrapper.get("[data-testid='setting-more-demo']");
    expect(more.attributes("aria-expanded")).toBe("false");
    await more.trigger("click");

    expect(wrapper.get("[data-testid='setting-more-text-demo']").text()).toBe("The full text, as it always was.");
    expect(more.attributes("aria-expanded")).toBe("true");
    await more.trigger("click");
    expect(wrapper.find("[data-testid='setting-more-text-demo']").exists()).toBe(false);
  });

  it("has no More link without a full text, and carries a stable id", () => {
    const wrapper = mount(SettingRow, { props: { id: "plain", title: "Plain", summary: "Just this." } });
    expect(wrapper.find("[data-testid='setting-more-plain']").exists()).toBe(false);
    expect(wrapper.attributes("id")).toBe("settings-row-plain");
    expect(wrapper.attributes("data-setting-row")).toBe("plain");
  });

  it("says what it adds with an Open link, and emits open", async () => {
    const wrapper = mount(SettingRow, { props: { id: "lab", title: "Lab", adds: "Nightly run in Memory" } });
    expect(wrapper.get("[data-testid='setting-adds-lab']").text()).toContain("Adds: Nightly run in Memory");
    await wrapper.get("[data-testid='setting-open-lab']").trigger("click");
    expect(wrapper.emitted("open")).toHaveLength(1);
  });

  it("shows nothing about adds without it, and no Open link when there is nowhere to go", () => {
    expect(mount(SettingRow, { props: { id: "a", title: "A" } }).find("[data-testid='setting-adds-a']").exists()).toBe(false);
    const wrapper = mount(SettingRow, { props: { id: "b", title: "B", adds: "A tool", noOpen: true } });
    expect(wrapper.find("[data-testid='setting-open-b']").exists()).toBe(false);
  });
});

describe("SettingSwitch and SettingGroup", () => {
  it("passes aria-label and test id to the switch button and emits click", async () => {
    const wrapper = mount(SettingSwitch, { props: { checked: true }, attrs: { "aria-label": "Show starters", "data-testid": "starters-switch" } });
    const button = wrapper.get("[data-testid='starters-switch']");
    expect(button.attributes("aria-label")).toBe("Show starters");
    expect(button.attributes("aria-checked")).toBe("true");
    await button.trigger("click");
    expect(wrapper.emitted("click")).toHaveLength(1);
  });

  it("titles a group and gives it a stable id", () => {
    const wrapper = mount(SettingGroup, { props: { id: "away", title: "While you're away", hint: "hint" }, slots: { default: "<p>row</p>" } });
    expect(wrapper.attributes("id")).toBe("settings-group-away");
    expect(wrapper.text()).toContain("While you're away");
    expect(wrapper.text()).toContain("hint");
  });
});
