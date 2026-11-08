import { mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import { defineComponent, h } from "vue";
import AutocompletePopup from "@/components/session/AutocompletePopup.vue";
import type { AutocompleteItem, AutocompleteMachine } from "@/composables/use-autocomplete";

function sessionItem(id: string, title: string, machine?: AutocompleteMachine): AutocompleteItem {
  return {
    id: `session:${id}`,
    label: title,
    description: "harbour-app · OpenCode 2",
    group: "session",
    value: `session:${id}`,
    session: { id, title, status: "idle", description: "harbour-app · OpenCode 2", updatedAt: 0 },
    ...(machine ? { machine } : {}),
  };
}

function render(items: AutocompleteItem[]) {
  return mount(defineComponent(() => () => h(AutocompletePopup, {
    open: true,
    items,
    isLoading: false,
    selectedValue: items[0]?.value ?? null,
    onSelect: () => {},
    ownMachineHint: "this session's machine",
  })));
}

describe("AutocompletePopup with sessions on other machines", () => {
  it("with no other machine, shows sessions under one heading and no machine names", () => {
    const wrapper = render([sessionItem("a", "Buoy telemetry retries"), sessionItem("b", "Fog horn scheduler")]);

    expect(wrapper.findAll("[data-testid=autocomplete-machine]")).toHaveLength(0);
    expect(wrapper.findAll(".autocomplete-popup__group-label")).toHaveLength(1);
    expect(wrapper.findAll("[role=option]")).toHaveLength(2);
  });

  it("names each machine before its sessions, and under a machine it can't reach says why with a way to fix it", async () => {
    const open = vi.fn();
    const falcon: AutocompleteMachine = { key: "home", id: null, name: "falcon", own: true };
    const kestrel: AutocompleteMachine = { key: "m-kestrel", id: "m-kestrel", name: "kestrel", own: false, unreachable: null };
    const hangar: AutocompleteMachine = {
      key: "m-hangar", id: "m-hangar", name: "hangar", own: false,
      unreachable: "falcon can't reach hangar, so its agent couldn't read these.",
      fix: { label: "Let falcon reach hangar", open },
    };
    const wrapper = render([
      sessionItem("a", "Buoy telemetry retries", falcon),
      sessionItem("k", "Tide chart caching", kestrel),
      sessionItem("h", "Runway lights audit", hangar),
    ]);

    expect(wrapper.findAll("[data-testid=autocomplete-machine]").map((row) => row.text())).toEqual([
      "falconthis session's machine",
      "kestrel",
      "hangar",
    ]);
    const options = wrapper.findAll("[role=option]");
    expect(options.map((option) => option.attributes("disabled") !== undefined)).toEqual([false, false, true]);
    const note = wrapper.get("[data-testid=autocomplete-machine-note]");
    expect(note.text()).toBe("falcon can't reach hangar, so its agent couldn't read these. Let falcon reach hangar");

    await note.get("a").trigger("click");
    expect(open).toHaveBeenCalledOnce();
  });
});
