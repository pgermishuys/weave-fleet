import { defineComponent, h, nextTick } from "vue";
import { mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import ModComposerBand from "@/components/mods/ModComposerBand.vue";
import ModPaneSheet from "../ModPaneSheet.vue";
import SessionMenuSheet from "../SessionMenuSheet.vue";
import { composerBands, modPanes, modPaneViewId, type ModPaneView } from "@/lib/mods/points";
import type { ModWireElement } from "@/lib/mods/types";

// The sheet's own behaviour (gestures, history) isn't under test: just what it holds.
const BottomSheet = defineComponent({
  props: { open: Boolean },
  setup: (props, { slots }) => () => (props.open ? h("div", [slots.head?.(), slots.default?.()]) : null),
});
const global = { stubs: { BottomSheet } };

const text = (value: string): ModWireElement => ({ type: "Text", props: {}, children: [value] });
const button = (): ModWireElement => ({ type: "Button", props: { key: "go", label: "Go" }, handles: { onPress: "h1" } });
const kept = [{ name: "Panel mod", draft: false }];
const draft = [{ name: "Panel mod", draft: true }];

function pane(paneId: string, extra: Partial<ModPaneView> = {}): ModPaneView {
  return { sessionId: "s1", paneId, title: `Pane ${paneId}`, mod: "panel", tree: text(`Body ${paneId}`), mods: kept, ...extra };
}

const viewOf = (paneId: string, mod = "panel") => modPaneViewId("s1", mod, paneId);

const menuProps = { open: true, title: "Fix", machineName: "atlas", changedFiles: 0, working: false, supportsSide: true, canFork: true, canOpenOnComputer: true };

beforeEach(() => {
  modPanes.clear();
  composerBands.clear();
});

describe("Session menu with panes", () => {
  it("lists no panes when there are none (today's DOM: SessionMenuSheet.characterise.test.ts)", () => {
    const without = mount(SessionMenuSheet, { props: menuProps, global }).html();
    const empty = mount(SessionMenuSheet, { props: { ...menuProps, panes: [] }, global }).html();
    expect(empty).toBe(without);
    expect(without).not.toContain("menu-pane");
  });

  it("lists each open pane, marks drafts, and emits the pane to open", async () => {
    const menu = mount(SessionMenuSheet, {
      props: { ...menuProps, panes: [{ id: "a", title: "Pane a", draft: false }, { id: "b", title: "Pane b", draft: true }] },
      global,
    });
    const items = menu.findAll("[data-testid=menu-pane]");
    expect(items.map((item) => item.text().replace("Draft", "").trim())).toEqual(["Pane a", "Pane b"]);
    expect(items[0]?.find("[data-testid=mod-draft-mark]").exists()).toBe(false);
    expect(items[1]?.find("[data-testid=mod-draft-mark]").exists()).toBe(true);
    await items[1]?.trigger("click");
    expect(menu.emitted("pane")).toEqual([["b"]]);
    expect(menu.emitted("pick")).toBeUndefined();
  });
});

describe("Pane sheet", () => {
  it("draws the pane's tree under its title", () => {
    modPanes.contribute("t", [pane("a")]);
    const sheet = mount(ModPaneSheet, { props: { open: true, sessionId: "s1", viewId: viewOf("a") }, global });
    expect(sheet.text()).toContain("Pane a");
    expect(sheet.find("[data-testid=mod-pane-body]").text()).toContain("Body a");
    expect(sheet.find("[data-testid=mod-draft-mark]").exists()).toBe(false);
  });

  it("marks a draft pane", () => {
    modPanes.contribute("t", [pane("a", { mods: draft })]);
    const sheet = mount(ModPaneSheet, { props: { open: true, sessionId: "s1", viewId: viewOf("a") }, global });
    expect(sheet.find("[data-testid=mod-draft-mark]").exists()).toBe(true);
  });

  it("sends actions from the phone", async () => {
    const onAction = vi.fn();
    modPanes.contribute("t", [pane("a", { tree: button(), onAction })]);
    const sheet = mount(ModPaneSheet, { props: { open: true, sessionId: "s1", viewId: viewOf("a") }, global });
    await sheet.find("[data-testid=mod-pane-body] button").trigger("click");
    expect(onAction).toHaveBeenCalledWith({ handle: "h1", kind: "press" }, "phone");
  });

  it("closes when the pane is removed while open", async () => {
    const remove = modPanes.contribute("t", [pane("a")]);
    const sheet = mount(ModPaneSheet, { props: { open: true, sessionId: "s1", viewId: viewOf("a") }, global });
    expect(sheet.find("[data-testid=mod-pane-body]").exists()).toBe(true);
    remove();
    await nextTick();
    expect(sheet.find("[data-testid=mod-pane-body]").exists()).toBe(false);
    expect(sheet.findComponent(BottomSheet).props("open")).toBe(false);
  });

  it("draws the pane of the mod it names when two mods use one pane id", () => {
    modPanes.contribute("one", [pane("a", { mod: "one", tree: text("From one") })]);
    modPanes.contribute("two", [pane("a", { mod: "two", tree: text("From two") })]);
    const sheet = mount(ModPaneSheet, { props: { open: true, sessionId: "s1", viewId: viewOf("a", "two") }, global });
    expect(sheet.find("[data-testid=mod-pane-body]").text()).toBe("From two");
  });

  it("draws nothing for another session's pane", () => {
    modPanes.contribute("t", [pane("a", { sessionId: "s2" })]);
    const sheet = mount(ModPaneSheet, { props: { open: true, sessionId: "s1", viewId: viewOf("a") }, global });
    expect(sheet.find("[data-testid=mod-pane-body]").exists()).toBe(false);
  });
});

describe("Phone composer band", () => {
  it("draws on the phone surface and sends actions from it", async () => {
    const onAction = vi.fn();
    composerBands.contribute("t", [{ sessionId: "s1", tree: button(), mods: draft, onAction }]);
    const band = mount(ModComposerBand, { props: { sessionId: "s1", surface: "phone" } });
    expect(band.find("[data-testid=mod-draft-mark]").exists()).toBe(true);
    await band.find("button").trigger("click");
    expect(onAction).toHaveBeenCalledWith({ handle: "h1", kind: "press" }, "phone");
  });

  it("renders no element for no contribution, a null tree or an invalid one", () => {
    const render = () => mount(ModComposerBand, { props: { sessionId: "s1", surface: "phone" } }).element.nodeType;
    expect(render()).toBe(Node.COMMENT_NODE);
    composerBands.contribute("t", [{ sessionId: "s1", tree: null, mods: kept }]);
    expect(render()).toBe(Node.COMMENT_NODE);
    composerBands.clear();
    composerBands.contribute("t", [{ sessionId: "s1", tree: { type: "Nope", props: {} } as unknown as ModWireElement, mods: kept }]);
    expect(render()).toBe(Node.COMMENT_NODE);
  });
});

describe("Phone session page", () => {
  it("draws the band immediately before the composer", async () => {
    const { readFileSync } = await import("node:fs");
    const source = readFileSync(`${process.cwd()}/src/components/phone/session/PhoneSessionPage.vue`, "utf8");
    const band = source.indexOf("<ModComposerBand");
    const composer = source.indexOf("<PhoneComposer");
    expect(band).toBeGreaterThan(0);
    expect(source.slice(band, composer)).toMatch(/^<ModComposerBand[^<]*\/>\s*$/);
  });
});
