import { flushPromises, mount } from "@vue/test-utils";
import { defineComponent, h, markRaw, shallowRef } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { WeaveConfigView, WeaveInstall, WeavePluginChange } from "@/api/client";
import HarnessWeaveGroup from "@/components/settings/HarnessWeaveGroup.vue";

const view = shallowRef<WeaveConfigView | null>(null);
const error = shallowRef<string | null>(null);
const load = vi.fn(async () => {});
const changePlugin = vi.fn(async (): Promise<WeavePluginChange> => { throw new Error("not set up"); });

const Inline = defineComponent({ setup: (_, { slots }) => () => h("div", slots.default?.()) });

const weave: WeaveInstall = {
  flavor: "weave",
  package: "@weaveio/weave-adapter-opencode",
  entry: "@weaveio/weave-adapter-opencode@0.2.0-next.1",
  acceptsFleetConfig: true,
};
const v2Entry = "@weaveio/weave-adapter-opencode2@0.2.0-next.3";
const v2Install: WeaveInstall = {
  flavor: "weave",
  package: "@weaveio/weave-adapter-opencode2",
  entry: v2Entry,
  acceptsFleetConfig: true,
  addedByFleet: true,
};

function config(overrides: Partial<WeaveConfigView> = {}): WeaveConfigView {
  return {
    source: "own",
    files: {},
    updatedAt: null,
    harnesses: [{ harnessType: "opencode", harnessName: "OpenCode", checked: true, installs: [weave] }],
    apply: null,
    ...overrides,
  };
}

const without = config({
  harnesses: [
    { harnessType: "opencode2", harnessName: "OpenCode 2", checked: true, installs: [], addTo: "/home/you/.weave/harnesses/opencode2/config/opencode.json" },
    { harnessType: "opencode", harnessName: "OpenCode", checked: true, installs: [], addTo: "/home/you/.config/opencode/opencode.json" },
  ],
});

async function mountGroup(harnessType: string, initial: WeaveConfigView | null) {
  view.value = initial;
  const state = markRaw({ view, loading: shallowRef(false), error, load, check: vi.fn(), save: vi.fn(), readOwn: vi.fn(), changePlugin });
  const wrapper = mount(HarnessWeaveGroup as any, { // eslint-disable-line @typescript-eslint/no-explicit-any
    props: { harnessType, weave: state },
    global: { stubs: { Dialog: Inline, DialogContent: Inline, DialogHeader: Inline, DialogTitle: Inline, DialogDescription: Inline } },
  });
  await flushPromises();
  return wrapper;
}

beforeEach(() => {
  view.value = null;
  error.value = null;
  vi.clearAllMocks();
});

describe("HarnessWeaveGroup", () => {
  it("shows which Weave this harness loads, and only this harness's", async () => {
    const wrapper = await mountGroup("opencode", config({
      harnesses: [
        { harnessType: "opencode", harnessName: "OpenCode", checked: true, installs: [weave] },
        { harnessType: "claude-code", harnessName: "Claude Code", checked: false, installs: [], note: "Fleet doesn't hand Weave a config in Claude Code yet." },
      ],
    }));

    expect(wrapper.text()).toContain("@weaveio/weave-adapter-opencode@0.2.0-next.1");
    expect(wrapper.text()).not.toContain("Fleet doesn't hand Weave");
    expect(wrapper.get("[data-setting-group='weave']").text()).toContain("Weave");
  });

  it("is absent for a harness Fleet can't ask about Weave, and before Fleet has asked", async () => {
    expect((await mountGroup("pi", config())).find("[data-setting-group='weave']").exists()).toBe(false);
    expect((await mountGroup("opencode", null)).find("[data-setting-group='weave']").exists()).toBe(false);
  });

  it("says why it can't be asked when Fleet couldn't load Weave", async () => {
    error.value = "HTTP 500";
    const wrapper = await mountGroup("opencode", null);

    expect(wrapper.get("[data-setting-group='weave']").text()).toContain("HTTP 500");
  });

  it("offers nothing to edit while no harness loads Weave", async () => {
    const wrapper = await mountGroup("opencode2", without);

    expect(wrapper.get("[data-testid='weave-edit-config']").attributes("disabled")).toBeDefined();
    expect(wrapper.text()).toContain("Weave isn't running in any harness yet");
  });

  it("opens the config dialog from Edit, and says whose config Weave reads", async () => {
    const wrapper = await mountGroup("opencode", config({ source: "fleet", files: { "config.weave": "" } }));

    const dialog = wrapper.findComponent({ name: "WeaveConfigDialog" });
    expect(wrapper.text()).toContain("Kept in Fleet");
    expect(dialog.props("open")).toBe(false);
    await wrapper.get("[data-testid='weave-edit-config']").trigger("click");
    expect(dialog.props("open")).toBe(true);
  });

  it("asks for an update when the installed Weave can't read Fleet's folder", async () => {
    const wrapper = await mountGroup("opencode", config({
      harnesses: [{
        harnessType: "opencode", harnessName: "OpenCode", checked: true,
        installs: [{ flavor: "legacy", package: "@opencode_weave/weave", entry: "@opencode_weave/weave@0.8.2", acceptsFleetConfig: false }],
      }],
    }));

    expect(wrapper.get("[data-testid='weave-outdated']").text()).toContain("Update Weave Legacy to 0.9.0");
  });

  it("says where Add Weave writes", async () => {
    const wrapper = await mountGroup("opencode2", without);

    expect(wrapper.text()).toContain("Add Weave puts it in /home/you/.weave/harnesses/opencode2/config/opencode.json.");
    expect(wrapper.find("[data-testid='weave-add-opencode2']").exists()).toBe(true);
    expect(wrapper.find("[data-testid='weave-add-opencode']").exists()).toBe(false);
  });

  it("adds Weave to the harness and says what happened", async () => {
    const after = config({ harnesses: [{ harnessType: "opencode2", harnessName: "OpenCode 2", checked: true, installs: [v2Install] }] });
    changePlugin.mockImplementation(async () => {
      view.value = after;
      return { configPath: "/cfg/opencode.json", entry: v2Entry, loaded: true, message: `Added ${v2Entry} to /cfg/opencode.json. OpenCode 2 loaded it.`, config: after };
    });
    const wrapper = await mountGroup("opencode2", without);

    await wrapper.get("[data-testid='weave-add-opencode2']").trigger("click");
    await flushPromises();

    expect(changePlugin).toHaveBeenCalledWith("opencode2", true);
    expect(wrapper.get("[data-testid='weave-plugin-notice']").text()).toBe(`Added ${v2Entry} to /cfg/opencode.json. OpenCode 2 loaded it.`);
    expect(wrapper.find("[data-testid='weave-remove-opencode2']").exists()).toBe(true);
  });

  it("shows why an add was refused", async () => {
    changePlugin.mockRejectedValue(new Error("Fleet couldn't look up @weaveio/weave-adapter-opencode2 on npm."));
    const wrapper = await mountGroup("opencode2", without);

    await wrapper.get("[data-testid='weave-add-opencode2']").trigger("click");
    await flushPromises();

    const notice = wrapper.get("[data-testid='weave-plugin-notice']");
    expect(notice.text()).toBe("Fleet couldn't look up @weaveio/weave-adapter-opencode2 on npm.");
    expect(notice.attributes("role")).toBe("alert");
  });

  it("offers Remove only for the entry Fleet added", async () => {
    const both = config({
      harnesses: [
        { harnessType: "opencode2", harnessName: "OpenCode 2", checked: true, installs: [v2Install] },
        { harnessType: "opencode", harnessName: "OpenCode", checked: true, installs: [weave] },
      ],
    });

    const v2 = await mountGroup("opencode2", both);
    expect(v2.find("[data-testid='weave-remove-opencode2']").exists()).toBe(true);
    const own = await mountGroup("opencode", both);
    expect(own.find("[data-testid='weave-remove-opencode']").exists()).toBe(false);
    expect(own.find("[data-testid='weave-add-opencode']").exists()).toBe(false);

    changePlugin.mockResolvedValue({ configPath: "/cfg/opencode.json", entry: v2Entry, loaded: true, message: "Took it out.", config: without });
    await v2.get("[data-testid='weave-remove-opencode2']").trigger("click");
    await flushPromises();
    expect(changePlugin).toHaveBeenCalledWith("opencode2", false);
  });

  it("says why a listed Weave didn't load", async () => {
    const wrapper = await mountGroup("opencode2", config({
      harnesses: [{
        harnessType: "opencode2",
        harnessName: "OpenCode 2",
        checked: true,
        installs: [{ ...v2Install, entry: "@weaveio/weave-adapter-opencode2@9.9.9", acceptsFleetConfig: false, error: "No matching version found" }],
      }],
    }));

    expect(wrapper.text()).toContain("@weaveio/weave-adapter-opencode2@9.9.9 · didn't load: No matching version found");
    expect(wrapper.text()).toContain("Weave · didn't load");
  });
});
