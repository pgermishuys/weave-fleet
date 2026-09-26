import { flushPromises, mount } from "@vue/test-utils";
import { defineComponent, h, shallowRef } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { WeaveConfigView, WeaveHarnessCheck, WeaveInstall, WeavePluginChange, WeaveSaveResult } from "@/api/client";
import WeaveSection from "@/components/settings/WeaveSection.vue";

const view = shallowRef<WeaveConfigView | null>(null);
const load = vi.fn(async () => {});
const check = vi.fn(async (): Promise<WeaveHarnessCheck[]> => []);
const save = vi.fn(async (): Promise<WeaveSaveResult> => ({ saved: true, checks: [], config: null }));
const readOwn = vi.fn();
const changePlugin = vi.fn(async (): Promise<WeavePluginChange> => { throw new Error("not set up"); });

vi.mock("@/composables/use-weave-config", () => ({
  useWeaveConfig: () => ({ view, loading: shallowRef(false), error: shallowRef(null), load, check, save, readOwn, changePlugin }),
}));

// CodeMirror needs layout jsdom doesn't have; a textarea stands in for the editor.
const EditorStub = defineComponent({
  props: { modelValue: { type: String, required: true }, label: { type: String, required: true }, filename: { type: String, default: "" } },
  emits: ["update:modelValue", "save"],
  setup(props, { emit }) {
    return () => h("textarea", {
      "data-testid": "weave-editor",
      "data-file": props.filename,
      value: props.modelValue,
      onInput: (event: Event) => emit("update:modelValue", (event.target as HTMLTextAreaElement).value),
    });
  },
});

const weave: WeaveInstall = {
  flavor: "weave",
  package: "@weaveio/weave-adapter-opencode",
  entry: "@weaveio/weave-adapter-opencode@0.2.0-next.1",
  acceptsFleetConfig: true,
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

async function mountSection(initial: WeaveConfigView) {
  view.value = initial;
  const wrapper = mount(WeaveSection, { global: { stubs: { ProfileConfigEditor: EditorStub } } });
  await flushPromises();
  return wrapper;
}

beforeEach(() => {
  view.value = null;
  vi.clearAllMocks();
});

describe("WeaveSection", () => {
  it("shows which Weave each harness loads", async () => {
    const wrapper = await mountSection(config({
      harnesses: [
        { harnessType: "opencode", harnessName: "OpenCode", checked: true, installs: [weave] },
        { harnessType: "claude-code", harnessName: "Claude Code", checked: false, installs: [], note: "Fleet doesn't hand Weave a config in Claude Code yet." },
      ],
    }));

    const rows = wrapper.get("[data-testid='weave-harnesses']").findAll("li");
    expect(rows[0]!.text()).toContain("@weaveio/weave-adapter-opencode@0.2.0-next.1");
    expect(rows[0]!.text()).toContain("Weave");
    expect(rows[1]!.text()).toContain("Not yet");
    expect(load).toHaveBeenCalled();
  });

  it("says so and offers nothing to edit when no harness loads Weave", async () => {
    const wrapper = await mountSection(config({
      harnesses: [{ harnessType: "opencode", harnessName: "OpenCode", checked: true, installs: [] }],
    }));

    expect(wrapper.get("[data-testid='weave-none']").text()).toContain("Weave isn't running in any harness");
    expect(wrapper.find("[data-testid='weave-source-fleet']").exists()).toBe(false);
  });

  it("asks for an update when the installed Weave can't read Fleet's folder", async () => {
    const wrapper = await mountSection(config({
      harnesses: [{
        harnessType: "opencode", harnessName: "OpenCode", checked: true,
        installs: [{ flavor: "legacy", package: "@opencode_weave/weave", entry: "@opencode_weave/weave@0.8.2", acceptsFleetConfig: false }],
      }],
    }));

    expect(wrapper.get("[data-testid='weave-outdated']").text()).toContain("Update Weave Legacy to 0.9.0");
    expect(wrapper.get("[data-testid='weave-source-fleet']").attributes("disabled")).toBeDefined();
  });

  it("starts Fleet's config from a short template and tries it with the harness", async () => {
    check.mockResolvedValueOnce([{
      harnessType: "opencode", harnessName: "OpenCode", flavor: "weave",
      check: { ok: true, agents: ["loom", "reviewer"] },
    }]);
    const wrapper = await mountSection(config());

    await wrapper.get("[data-testid='weave-source-fleet']").trigger("click");
    const editor = wrapper.get("[data-testid='weave-editor']");
    expect(editor.attributes("data-file")).toBe("config.weave");
    expect((editor.element as HTMLTextAreaElement).value).toContain("Fleet's Weave config");

    await editor.setValue("agent reviewer {}");
    await wrapper.get("[data-testid='weave-test']").trigger("click");
    await flushPromises();

    expect(check).toHaveBeenCalledWith("weave", { "config.weave": "agent reviewer {}" });
    const result = wrapper.get("[data-testid='weave-check']");
    expect(result.text()).toContain("OpenCode loads 2 Weave agents");
    expect(result.text()).toContain("reviewer");
  });

  it("shows where Weave couldn't read the config, and that nothing was saved", async () => {
    save.mockResolvedValueOnce({
      saved: false,
      checks: [{
        harnessType: "opencode", harnessName: "OpenCode", flavor: "weave",
        check: { ok: false, agents: [], error: "OpenCode started, but Weave added no agents.", details: ["config.weave:2:1 UnclosedBlock"] },
      }],
      config: null,
    });
    const wrapper = await mountSection(config({ source: "fleet", files: { "config.weave": "agent a {}" } }));

    await wrapper.get("[data-testid='weave-editor']").setValue("agent a {");
    await wrapper.get("[data-testid='weave-save']").trigger("click");
    await flushPromises();

    expect(save).toHaveBeenCalledWith("fleet", { "config.weave": "agent a {" });
    const result = wrapper.get("[data-testid='weave-check']");
    expect(result.text()).toContain("config.weave:2:1 UnclosedBlock");
    expect(result.text()).toContain("Nothing was saved");
  });

  it("adds a prompt file next to the config", async () => {
    const wrapper = await mountSection(config({ source: "fleet", files: { "config.weave": "" } }));

    await wrapper.get("[data-testid='weave-add-prompt']").trigger("click");
    await wrapper.get("[data-testid='weave-new-prompt']").setValue("team");
    await wrapper.get("form").trigger("submit");

    expect(wrapper.get("[data-testid='weave-tab-prompts/team.md']").attributes("aria-selected")).toBe("true");
    expect(wrapper.get("[data-testid='weave-editor']").attributes("data-file")).toBe("prompts/team.md");
  });

  it("lists running folders until each has reloaded", async () => {
    const wrapper = await mountSection(config({
      source: "fleet",
      files: { "config.weave": "" },
      apply: { folders: [{ directory: "/work/alpha", reloaded: true }, { directory: "/work/beta", reloaded: false }] },
    }));

    const apply = wrapper.get("[data-testid='weave-apply']");
    expect(apply.text()).toContain("Applied to 1 of 2 running folders");
    expect(apply.text()).toContain("Reloaded");
    expect(apply.text()).toContain("Waiting · turn running");
  });

  it("switching back to the user's own file is saved as such", async () => {
    const wrapper = await mountSection(config({ source: "fleet", files: { "config.weave": "x" } }));

    await wrapper.get("[data-testid='weave-source-own']").trigger("click");
    await wrapper.get("[data-testid='weave-save']").trigger("click");
    await flushPromises();

    expect(save).toHaveBeenCalledWith("own", { "config.weave": "x" });
  });

  describe("Add Weave", () => {
    const v2Entry = "@weaveio/weave-adapter-opencode2@0.2.0-next.3";
    const v2Install: WeaveInstall = {
      flavor: "weave",
      package: "@weaveio/weave-adapter-opencode2",
      entry: v2Entry,
      acceptsFleetConfig: true,
      addedByFleet: true,
    };
    const without = config({
      harnesses: [
        { harnessType: "opencode", harnessName: "OpenCode", checked: true, installs: [], addTo: "/home/you/.config/opencode/opencode.json" },
        { harnessType: "opencode2", harnessName: "OpenCode 2", checked: true, installs: [], addTo: "/home/you/.weave/harnesses/opencode2/config/opencode.json" },
      ],
    });

    it("lists OpenCode 2 first and says where Add Weave writes", async () => {
      const wrapper = await mountSection(without);

      const rows = wrapper.get("[data-testid='weave-harnesses']").findAll("li");
      expect(rows[0]!.text()).toContain("OpenCode 2");
      expect(rows[0]!.text()).toContain("Add Weave puts it in /home/you/.weave/harnesses/opencode2/config/opencode.json.");
      expect(wrapper.find("[data-testid='weave-add-opencode2']").exists()).toBe(true);
      expect(wrapper.get("[data-testid='weave-none']").text()).toContain("Add Weave above puts it there for you.");
    });

    it("adds Weave to the harness and says what happened", async () => {
      const after = config({
        harnesses: [{ harnessType: "opencode2", harnessName: "OpenCode 2", checked: true, installs: [v2Install] }],
      });
      changePlugin.mockImplementation(async () => {
        view.value = after;
        return { configPath: "/cfg/opencode.json", entry: v2Entry, loaded: true, message: `Added ${v2Entry} to /cfg/opencode.json. OpenCode 2 loaded it.`, config: after };
      });
      const wrapper = await mountSection(without);

      await wrapper.get("[data-testid='weave-add-opencode2']").trigger("click");
      await flushPromises();

      expect(changePlugin).toHaveBeenCalledWith("opencode2", true);
      expect(wrapper.get("[data-testid='weave-plugin-notice']").text()).toBe(`Added ${v2Entry} to /cfg/opencode.json. OpenCode 2 loaded it.`);
      expect(wrapper.find("[data-testid='weave-remove-opencode2']").exists()).toBe(true);
      expect(wrapper.find("[data-testid='weave-none']").exists()).toBe(false);
    });

    it("shows why an add was refused", async () => {
      changePlugin.mockRejectedValue(new Error("Fleet couldn't look up @weaveio/weave-adapter-opencode2 on npm."));
      const wrapper = await mountSection(without);

      await wrapper.get("[data-testid='weave-add-opencode2']").trigger("click");
      await flushPromises();

      const notice = wrapper.get("[data-testid='weave-plugin-notice']");
      expect(notice.text()).toBe("Fleet couldn't look up @weaveio/weave-adapter-opencode2 on npm.");
      expect(notice.attributes("role")).toBe("alert");
    });

    it("offers Remove only for the entry Fleet added", async () => {
      const wrapper = await mountSection(config({
        harnesses: [
          { harnessType: "opencode2", harnessName: "OpenCode 2", checked: true, installs: [v2Install] },
          { harnessType: "opencode", harnessName: "OpenCode", checked: true, installs: [weave] },
        ],
      }));

      expect(wrapper.find("[data-testid='weave-remove-opencode2']").exists()).toBe(true);
      expect(wrapper.find("[data-testid='weave-remove-opencode']").exists()).toBe(false);
      expect(wrapper.find("[data-testid='weave-add-opencode']").exists()).toBe(false);

      changePlugin.mockResolvedValue({ configPath: "/cfg/opencode.json", entry: v2Entry, loaded: true, message: "Took it out.", config: without });
      await wrapper.get("[data-testid='weave-remove-opencode2']").trigger("click");
      await flushPromises();
      expect(changePlugin).toHaveBeenCalledWith("opencode2", false);
    });

    it("says why a listed Weave didn't load", async () => {
      const wrapper = await mountSection(config({
        harnesses: [{
          harnessType: "opencode2",
          harnessName: "OpenCode 2",
          checked: true,
          installs: [{ ...v2Install, entry: "@weaveio/weave-adapter-opencode2@9.9.9", acceptsFleetConfig: false, error: "No matching version found" }],
        }],
      }));

      const row = wrapper.get("[data-testid='weave-harnesses']").get("li");
      expect(row.text()).toContain("@weaveio/weave-adapter-opencode2@9.9.9 · didn't load: No matching version found");
      expect(row.text()).toContain("Weave · didn't load");
    });
  });
});
