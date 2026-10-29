import { flushPromises, mount } from "@vue/test-utils";
import { defineComponent, h, markRaw, shallowRef } from "vue";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { WeaveConfigView, WeaveInstall, WeavePluginChange } from "@/api/client";
import HarnessWeaveGroup from "@/components/settings/HarnessWeaveGroup.vue";

const view = shallowRef<WeaveConfigView | null>(null);
const error = shallowRef<string | null>(null);
const load = vi.fn(async () => {});
const ensureLoaded = vi.fn();
const changingPlugin = shallowRef<string | null>(null);
const pluginNotices = shallowRef<Record<string, { text: string; tone: "ok" | "warn" | "error" }>>({});
function setPluginNotice(harnessType: string, notice: { text: string; tone: "ok" | "warn" | "error" } | null): void {
  const next = { ...pluginNotices.value };
  if (notice) next[harnessType] = notice;
  else delete next[harnessType];
  pluginNotices.value = next;
}
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
  const state = markRaw({
    view, loading: shallowRef(false), error, load, ensureLoaded, check: vi.fn(), save: vi.fn(), readOwn: vi.fn(), changePlugin,
    changingPlugin, pluginNotices, setPluginNotice,
  });
  const wrapper = mount(HarnessWeaveGroup as any, { // eslint-disable-line @typescript-eslint/no-explicit-any
    props: { harnessType, weave: state },
    attachTo: document.body,
    global: { stubs: { Dialog: Inline, DialogContent: Inline, DialogHeader: Inline, DialogTitle: Inline, DialogDescription: Inline } },
  });
  mounted.push(wrapper);
  await flushPromises();
  return wrapper;
}

const mounted: { unmount: () => void }[] = [];

beforeEach(() => {
  view.value = null;
  error.value = null;
  changingPlugin.value = null;
  pluginNotices.value = {};
  vi.clearAllMocks();
});

afterEach(() => {
  for (const wrapper of mounted.splice(0)) wrapper.unmount();
  vi.unstubAllGlobals();
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

  it("is absent for a harness Fleet can't ask about Weave", async () => {
    expect((await mountGroup("pi", config())).find("[data-setting-group='weave']").exists()).toBe(false);
  });

  it("says it's asking the harnesses until Fleet answers", async () => {
    const wrapper = await mountGroup("opencode", null);

    expect(wrapper.get("[data-setting-group='weave']").text()).toContain("Asking your harnesses which plugins they load…");
  });

  it("asks only once the group is seen, since asking can start a harness", async () => {
    let seen: (entries: { isIntersecting: boolean }[]) => void = () => {};
    vi.stubGlobal("IntersectionObserver", class {
      constructor(callback: typeof seen) { seen = callback; }
      observe() {}
      disconnect() {}
    });
    await mountGroup("opencode", null);
    expect(ensureLoaded).not.toHaveBeenCalled();

    seen([{ isIntersecting: true }]);
    expect(ensureLoaded).toHaveBeenCalledTimes(1);
  });

  it("doesn't ask again when Fleet has answered already", async () => {
    await mountGroup("opencode", config());

    expect(ensureLoaded).not.toHaveBeenCalled();
  });

  it("says why it can't be asked when Fleet couldn't load Weave", async () => {
    error.value = "HTTP 500";
    const wrapper = await mountGroup("opencode", null);

    expect(wrapper.get("[data-setting-group='weave']").text()).toContain("HTTP 500");
  });

  it("says Weave isn't running in any harness, what it adds, and offers nothing to edit", async () => {
    const wrapper = await mountGroup("opencode2", without);

    const none = wrapper.get("[data-testid='weave-none']");
    expect(none.text()).toContain("Weave isn't running in any harness");
    expect(none.text()).toContain("Add Weave above puts it there for you.");
    expect(none.get("a").attributes("href")).toBe("https://tryweave.io/docs/quickstart/");
    expect(wrapper.find("[data-testid='weave-edit-config']").exists()).toBe(false);
  });

  it("asks for the config dialog from Edit, and says whose config Weave reads", async () => {
    const wrapper = await mountGroup("opencode", config({ source: "fleet", files: { "config.weave": "" } }));

    expect(wrapper.text()).toContain("Kept in Fleet");
    await wrapper.get("[data-testid='weave-edit-config']").trigger("click");
    expect(wrapper.emitted("editConfig")).toHaveLength(1);
  });

  it("says why Check again failed, even with what Fleet found before still shown", async () => {
    const wrapper = await mountGroup("opencode", config());
    error.value = "HTTP 500";
    await flushPromises();

    const failed = wrapper.get("[data-testid='weave-error']");
    expect(failed.text()).toContain("HTTP 500");
    expect(failed.attributes("role")).toBe("alert");
  });

  it("asks for an update only on the tab whose harness loads the old Weave", async () => {
    const mixed = config({
      harnesses: [
        { harnessType: "opencode2", harnessName: "OpenCode 2", checked: true, installs: [v2Install] },
        {
          harnessType: "opencode", harnessName: "OpenCode", checked: true,
          installs: [{ flavor: "legacy", package: "@opencode_weave/weave", entry: "@opencode_weave/weave@0.8.2", acceptsFleetConfig: false }],
        },
      ],
    });

    expect((await mountGroup("opencode2", mixed)).find("[data-testid='weave-outdated']").exists()).toBe(false);
    expect((await mountGroup("opencode", mixed)).find("[data-testid='weave-outdated']").exists()).toBe(true);
  });

  it("holds Add Weave while another harness's plugin list is being changed", async () => {
    changingPlugin.value = "opencode";
    const wrapper = await mountGroup("opencode2", without);

    expect(wrapper.get("[data-testid='weave-add-opencode2']").attributes("disabled")).toBeDefined();
    expect(wrapper.get("[data-testid='weave-add-opencode2']").text()).toBe("Add Weave");
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
    // It didn't load, so its version isn't what's wrong.
    expect(wrapper.find("[data-testid='weave-outdated']").exists()).toBe(false);
  });
});
