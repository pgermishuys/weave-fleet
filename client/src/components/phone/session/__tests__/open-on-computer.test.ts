import { defineComponent, h } from "vue";
import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import OpenOnComputerCard from "../OpenOnComputerCard.vue";
import SessionMenuSheet from "../SessionMenuSheet.vue";

// The sheet's own behaviour (gestures, history) isn't under test: just what it holds.
const BottomSheet = defineComponent({ setup: (_, { slots }) => () => h("div", slots.default?.()) });
const global = { stubs: { BottomSheet } };

describe("Run a command sheet", () => {
  const props = { open: true, title: "Run a command", machineName: "atlas", homeName: "mini", supportsShell: true };

  it("sends a link to open a terminal on a machine with the web app", () => {
    const card = mount(OpenOnComputerCard, { props: { ...props, link: "http://127.0.0.2:5572/sessions/s1" }, global });

    expect(card.find("[data-testid=ooc-share]").text()).toContain("Open a terminal on atlas");
    expect(card.find("[data-testid=ooc-elsewhere]").exists()).toBe(false);
  });

  it("says to open the session from home on a machine without one", () => {
    const card = mount(OpenOnComputerCard, { props: { ...props, link: null }, global });

    expect(card.find("[data-testid=ooc-share]").exists()).toBe(false);
    expect(card.find("[data-testid=ooc-elsewhere]").text()).toBe("To open a terminal, open this session in Fleet on mini.");
    expect(card.find("[data-testid=ooc-command]").exists()).toBe(true);
  });
});

describe("Session menu", () => {
  const props = { open: true, title: "Fix the login", machineName: "atlas", changedFiles: 0, working: false, supportsSide: true, canFork: true };

  it("offers Open on my computer when the session's machine has a page for it", () => {
    const menu = mount(SessionMenuSheet, { props: { ...props, canOpenOnComputer: true }, global });

    expect(menu.find("[data-testid=menu-computer]").exists()).toBe(true);
  });

  it("leaves it out when the machine has no web app", () => {
    const menu = mount(SessionMenuSheet, { props: { ...props, canOpenOnComputer: false }, global });

    expect(menu.find("[data-testid=menu-computer]").exists()).toBe(false);
    expect(menu.find("[data-testid=menu-archive]").exists()).toBe(true);
  });
});
