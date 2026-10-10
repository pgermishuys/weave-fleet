import { flushPromises, mount } from "@vue/test-utils";
import { defineComponent, h } from "vue";
import { describe, expect, it, vi } from "vitest";
import type { ModFile } from "@/lib/mods/kept";

vi.mock("@/components/ui/dialog", () => {
  const pass = (name: string) => defineComponent({ name, setup: (_p, { slots }) => () => h("div", slots.default?.()) });
  return {
    Dialog: defineComponent({ name: "DialogStub", props: { open: Boolean }, setup: (props, { slots }) => () => (props.open ? h("div", slots.default?.()) : null) }),
    DialogContent: pass("DialogContent"),
    DialogDescription: pass("DialogDescription"),
    DialogHeader: pass("DialogHeader"),
    DialogTitle: pass("DialogTitle"),
  };
});

import ModCodeDialog from "@/components/mods/review/ModCodeDialog.vue";

const files: ModFile[] = [
  { path: "panel.html", content: "<p>hi</p>" },
  { path: "hooks.ts", content: "export const a = 1;\nconst b = `x`;" },
  { path: "mod.json", content: '{ "name": "test-chips" }' },
];

function mountDialog(load: () => Promise<ModFile[]>, open = true) {
  return mount(ModCodeDialog, { props: { open, title: "test-chips · draft", load } });
}

describe("ModCodeDialog", () => {
  it("loads on open, shows the title, and lists mod.json and the hooks file first", async () => {
    const load = vi.fn().mockResolvedValue(files);
    const wrapper = mountDialog(load);
    expect(wrapper.text()).toContain("test-chips · draft");
    await flushPromises();
    expect(load).toHaveBeenCalledTimes(1);
    const tabs = wrapper.findAll("[data-testid=mod-code-file]").map((tab) => tab.text());
    expect(tabs).toEqual(["mod.json", "hooks.ts", "panel.html"]);
    expect(wrapper.find("[data-testid=mod-code-body]").text()).toContain('"name"');
  });

  it("switches files", async () => {
    const wrapper = mountDialog(vi.fn().mockResolvedValue(files));
    await flushPromises();
    await wrapper.findAll("[data-testid=mod-code-file]")[1]!.trigger("click");
    expect(wrapper.find("[data-testid=mod-code-body]").text()).toContain("export const a = 1;");
    expect(wrapper.findAll("[data-testid=mod-code-file]")[1]!.attributes("aria-selected")).toBe("true");
  });

  it("does not load while closed, and loads when opened", async () => {
    const load = vi.fn().mockResolvedValue(files);
    const wrapper = mountDialog(load, false);
    await flushPromises();
    expect(load).not.toHaveBeenCalled();
    await wrapper.setProps({ open: true });
    await flushPromises();
    expect(load).toHaveBeenCalledTimes(1);
  });

  it("shows the loading state", () => {
    const wrapper = mountDialog(() => new Promise(() => {}));
    expect(wrapper.find("[data-testid=mod-code-loading]").exists()).toBe(true);
  });

  it("shows the server's message when loading fails", async () => {
    const wrapper = mountDialog(vi.fn().mockRejectedValue(new Error("That draft is gone.")));
    await flushPromises();
    expect(wrapper.find("[data-testid=mod-code-error]").text()).toBe("That draft is gone.");
  });

  it("says when there are no files", async () => {
    const wrapper = mountDialog(vi.fn().mockResolvedValue([]));
    await flushPromises();
    expect(wrapper.find("[data-testid=mod-code-empty]").exists()).toBe(true);
  });

  it("keeps a file with backticks inside its block", async () => {
    const wrapper = mountDialog(vi.fn().mockResolvedValue([{ path: "hooks.ts", content: "const s = ```x```;" }]));
    await flushPromises();
    expect(wrapper.find("[data-testid=mod-code-body]").text()).toContain("```x```");
  });
});
