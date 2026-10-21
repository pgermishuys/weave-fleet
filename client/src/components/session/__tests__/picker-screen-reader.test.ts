import { mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import AutocompletePopup from "@/components/session/AutocompletePopup.vue";
import ModelSelector from "@/components/session/ModelSelector.vue";
import { createModelSelectionKey, type ModelOption } from "@/composables/use-models";
import type { AutocompleteItem } from "@/composables/use-autocomplete";

vi.mock("@/api/client", () => ({ api: { GET: vi.fn(), PUT: vi.fn() } }));

const models: ModelOption[] = ["one", "two", "three"].map((id) => ({
  id, name: id, providerId: "p", selectionKey: createModelSelectionKey("p", id), provider: "P", description: "",
}));

describe("model picker for screen readers", () => {
  async function open() {
    const wrapper = mount(ModelSelector, { props: { models, modelValue: "", harnessType: "opencode" }, attachTo: document.body });
    await wrapper.get("button").trigger("click");
    return wrapper;
  }

  it("lists options with role=option, one selected", async () => {
    const wrapper = await open();
    const options = wrapper.findAll('[role="option"]');
    expect(options.length).toBeGreaterThanOrEqual(3);
    expect(options.filter((o) => o.attributes("aria-selected") === "true")).toHaveLength(1);
    wrapper.unmount();
  });

  it("keeps the filter out of the listbox and points it at the highlighted option", async () => {
    const wrapper = await open();
    const input = wrapper.get("input");
    const listbox = wrapper.get('[role="listbox"]');
    expect(listbox.find("input").exists()).toBe(false);
    expect(input.attributes("role")).toBe("combobox");
    expect(input.attributes("aria-controls")).toBe(listbox.attributes("id"));
    const active = () => input.attributes("aria-activedescendant");
    const first = active();
    expect(first).toBeTruthy();
    expect(wrapper.get(`#${first}`).attributes("role")).toBe("option");
    await input.trigger("keydown", { key: "ArrowDown" });
    expect(active()).toBeTruthy();
    expect(active()).not.toBe(first);
    wrapper.unmount();
  });
});

const items: AutocompleteItem[] = [
  { id: "a", value: "/compact", label: "/compact", group: "command" },
  { id: "b", value: "/undo", label: "/undo", group: "command" },
];

describe("autocomplete popup for screen readers", () => {
  it("gives every row role=option with an id, the selected one aria-selected", () => {
    const wrapper = mount(AutocompletePopup, {
      props: { open: true, items, isLoading: false, selectedValue: "/undo", onSelect: vi.fn() },
    });
    const options = wrapper.findAll('[role="option"]');
    expect(options).toHaveLength(2);
    expect(options.every((o) => o.attributes("id"))).toBe(true);
    expect(options.map((o) => o.attributes("aria-selected"))).toEqual(["false", "true"]);
  });
});
