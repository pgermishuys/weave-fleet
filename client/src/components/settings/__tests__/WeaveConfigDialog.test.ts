import { flushPromises, mount } from "@vue/test-utils";
import { defineComponent, h, markRaw, shallowRef } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { WeaveConfigView, WeaveHarnessCheck, WeaveInstall, WeaveSaveResult } from "@/api/client";
import WeaveConfigDialog from "@/components/settings/WeaveConfigDialog.vue";

const view = shallowRef<WeaveConfigView | null>(null);
const check = vi.fn(async (): Promise<WeaveHarnessCheck[]> => []);
const save = vi.fn(async (): Promise<WeaveSaveResult> => ({ saved: true, checks: [], config: null }));
const readOwn = vi.fn();

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

// The dialog's own parts draw inline here, so the test reads them without a portal.
const Inline = defineComponent({ setup: (_, { slots }) => () => h("div", slots.default?.()) });

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
  const state = markRaw({ view, loading: shallowRef(false), error: shallowRef(null), load: vi.fn(), check, save, readOwn, changePlugin: vi.fn() });
  const wrapper = mount(WeaveConfigDialog as any, { // eslint-disable-line @typescript-eslint/no-explicit-any
    props: { weave: state, open: true },
    global: {
      stubs: {
        ProfileConfigEditor: EditorStub,
        Dialog: Inline,
        DialogContent: Inline,
        DialogHeader: Inline,
        DialogTitle: Inline,
        DialogDescription: Inline,
      },
    },
  });
  await flushPromises();
  return wrapper;
}

beforeEach(() => {
  view.value = null;
  vi.clearAllMocks();
});

describe("WeaveConfigDialog", () => {
  it("can't keep the config in Fleet when the installed Weave can't read Fleet's folder", async () => {
    const wrapper = await mountSection(config({
      harnesses: [{
        harnessType: "opencode", harnessName: "OpenCode", checked: true,
        installs: [{ flavor: "legacy", package: "@opencode_weave/weave", entry: "@opencode_weave/weave@0.8.2", acceptsFleetConfig: false }],
      }],
    }));

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
});
