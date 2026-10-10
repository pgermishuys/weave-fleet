import { defineComponent, h } from "vue";
import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import SessionMenuSheet from "../SessionMenuSheet.vue";

/**
 * The session's ⋯ menu as it was before mods: today's DOM. Imports only what existed before mods, so it passes before
 * them and after them alike.
 */
const BottomSheet = defineComponent({
  props: { open: Boolean },
  setup: (props, { slots }) => () => (props.open ? h("div", [slots.head?.(), slots.default?.()]) : null),
});
const global = { stubs: { BottomSheet } };

/** The DOM without Vue's v-if/v-for placeholders. */
const html = (w: { element: Element }) => w.element.outerHTML.replace(/<!--[\s\S]*?-->/g, "");

const menuProps = { open: true, title: "Fix", machineName: "atlas", changedFiles: 0, working: false, supportsSide: true, canFork: true, canOpenOnComputer: true };

describe("Session menu", () => {
  it("draws as it always did", () => {
    expect(html(mount(SessionMenuSheet, { props: menuProps, global }))).toMatchSnapshot();
  });

  it("draws as it always did while working with changed files and no fork", () => {
    expect(html(mount(SessionMenuSheet, { props: { ...menuProps, changedFiles: 3, working: true, canFork: false, canOpenOnComputer: false }, global }))).toMatchSnapshot();
  });
});
