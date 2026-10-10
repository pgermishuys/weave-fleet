import { mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { defineComponent, h, nextTick } from "vue";
import { afterEach, describe, expect, it, vi } from "vitest";
import ModPaneCanvas from "@/components/canvas/ModPaneCanvas.vue";
import { modPanes } from "@/lib/mods/points";

const authors = [{ name: "ci-mod", draft: false }] as never;
const mountPane = (mod = "ci-mod") => mount(ModPaneCanvas, { props: { sessionId: "s1", paneId: "runs", mod } });
const contribute = (tree: unknown, onAction?: never, mod = "ci-mod", paneId = "runs") =>
  modPanes.contribute(`t-${mod}-${paneId}`, [{ sessionId: "s1", paneId, title: "Runs", mod, tree: tree as never, mods: authors, onAction }]);

describe("ModPaneCanvas", () => {
  afterEach(() => modPanes.clear());

  it("draws the tree", () => {
    contribute({ type: "Text", props: {}, children: ["3 passing"] });
    expect(mountPane().get("[data-testid=mod-tree]").text()).toContain("3 passing");
  });

  it("waits for the mod when it has no view, and draws once it does", async () => {
    const wrapper = mountPane();
    expect(wrapper.get("[data-testid=mod-pane-empty]").text()).toBe("Waiting for ci-mod to draw this pane");
    contribute({ type: "Text", props: {}, children: ["back"] });
    await wrapper.vm.$nextTick();
    expect(wrapper.find("[data-testid=mod-pane-empty]").exists()).toBe(false);
    expect(wrapper.text()).toContain("back");
  });

  it("is empty for a null tree and for an invalid one", () => {
    contribute(null);
    expect(mountPane().find("[data-testid=mod-tree]").exists()).toBe(false);
    expect(mountPane().find("[data-testid=mod-pane-empty]").exists()).toBe(true);
    modPanes.clear();
    contribute({ type: "Nope" });
    expect(mountPane().find("[data-testid=mod-pane-empty]").exists()).toBe(true);
  });

  it("sends actions as desktop", async () => {
    const onAction = vi.fn();
    contribute({ type: "Button", props: { key: "go", label: "Go" }, handles: { onPress: "h1" } }, onAction as never);
    await mountPane().get("button").trigger("click");
    expect(onAction).toHaveBeenCalledWith({ handle: "h1", kind: "press" }, "desktop");
  });

  it("draws its own mod's pane when two mods use the same pane id", () => {
    contribute({ type: "Text", props: {}, children: ["from one"] }, undefined, "mod-one");
    contribute({ type: "Text", props: {}, children: ["from two"] }, undefined, "mod-two");
    expect(mountPane("mod-one").get("[data-testid=mod-tree]").text()).toContain("from one");
    expect(mountPane("mod-two").get("[data-testid=mod-tree]").text()).toContain("from two");
    expect(mountPane("mod-three").find("[data-testid=mod-tree]").exists()).toBe(false);
  });

  it("does not redraw for another pane's view", async () => {
    setActivePinia(createPinia());
    contribute({ type: "Text", props: {}, children: ["mine"] });
    const state = { updates: 0 };
    const Host = defineComponent({
      setup: () => () => h(ModPaneCanvas, { sessionId: "s1", paneId: "runs", mod: "ci-mod", onVnodeUpdated: () => { state.updates++; } }),
    });
    const wrapper = mount(Host);
    await nextTick();
    contribute({ type: "Text", props: {}, children: ["other"] }, undefined, "ci-mod", "other-pane");
    await nextTick();
    expect(state.updates).toBe(0);
    contribute({ type: "Text", props: {}, children: ["mine, again"] });
    await nextTick();
    expect(wrapper.text()).toContain("mine, again");
  });
});
