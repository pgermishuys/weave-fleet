import { flushPromises, mount } from "@vue/test-utils";
import { defineComponent, h } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { HarnessProfile } from "@/api/client";
import HarnessProfilesPanel from "@/components/settings/HarnessProfilesPanel.vue";
import { useHarnessProfilesStore } from "@/stores/harness-profiles";

// CodeMirror needs layout jsdom doesn't have; a textarea stands in for the config editor.
const ConfigEditorStub = defineComponent({
  props: { modelValue: { type: String, required: true }, label: { type: String, required: true } },
  emits: ["update:modelValue", "save"],
  setup(props, { emit }) {
    return () => h("textarea", {
      "data-testid": "profile-config-editor",
      value: props.modelValue,
      onInput: (event: Event) => emit("update:modelValue", (event.target as HTMLTextAreaElement).value),
    });
  },
});

function profile(id: string, name: string, extra: Partial<HarnessProfile> = {}): HarnessProfile {
  return {
    id, harnessType: "opencode", name, content: `{ "model": "${id}/model" }`, isDefault: false,
    openSessions: 0, createdAt: "", updatedAt: "", ...extra,
  };
}

function mountPanel() {
  return mount(HarnessProfilesPanel, {
    props: { harnessType: "opencode", harnessName: "OpenCode" },
    global: { stubs: { ProfileConfigEditor: ConfigEditorStub } },
  });
}

let store: ReturnType<typeof useHarnessProfilesStore>;

beforeEach(() => {
  store = useHarnessProfilesStore();
  vi.spyOn(store, "load").mockResolvedValue();
});

describe("HarnessProfilesPanel", () => {
  it("explains profiles when there are none", async () => {
    store.byHarness = { opencode: [] };
    const view = mountPanel();
    await flushPromises();

    expect(view.get("[data-testid='harness-profiles-empty']").text()).toContain("No profiles yet");
  });

  it("lists profiles with the default, what each changes and who uses it", async () => {
    store.byHarness = { opencode: [profile("work", "Work", { isDefault: true, openSessions: 2 }), profile("local", "Local")] };
    const view = mountPanel();
    await flushPromises();

    const work = view.get("[data-testid='harness-profile-row-work']");
    expect(work.text()).toContain("Default");
    expect(work.text()).toContain("work/model");
    expect(work.text()).toContain("Used by 2 open sessions");
    expect(view.get("[data-testid='harness-profile-row-local']").text()).toContain("No open sessions");
    expect(view.get("[data-testid='harness-profile-row-none']").text()).toContain("No profile");
  });

  it("makes a profile the default", async () => {
    store.byHarness = { opencode: [profile("work", "Work"), profile("local", "Local")] };
    const setDefault = vi.spyOn(store, "setDefault").mockResolvedValue();
    const view = mountPanel();
    await flushPromises();

    const button = view.get("[data-testid='harness-profile-row-local']").findAll("button").find((b) => b.text().includes("Set default"));
    await button!.trigger("click");
    await flushPromises();

    expect(setDefault).toHaveBeenCalledWith("opencode", "local");
    expect(view.get("[data-testid='harness-profile-notice']").text()).toBe("New sessions start with Local.");
  });

  it("saves an edit and says open sessions keep the old version until Fleet restarts", async () => {
    store.byHarness = { opencode: [profile("work", "Work", { openSessions: 1 })] };
    const update = vi.spyOn(store, "update").mockImplementation(async (_type, id, name, content) =>
      profile(id, name, { content, openSessions: 1 }));
    const view = mountPanel();
    await flushPromises();

    await view.get("[data-testid='harness-profile-edit-work']").trigger("click");
    await view.get("[data-testid='profile-config-editor']").setValue(`{ "model": "work/other" }`);
    await view.get("[data-testid='harness-profile-save']").trigger("click");
    await flushPromises();

    expect(update).toHaveBeenCalledWith("opencode", "work", "Work", `{ "model": "work/other" }`);
    expect(view.get("[data-testid='harness-profile-notice']").text())
      .toBe("Saved Work. New sessions use it now; the 1 open session picks it up when Fleet restarts.");
    expect(view.find("[data-testid='harness-profile-editor']").exists()).toBe(false);
  });

  it("keeps the editor open and shows OpenCode's reason when saving fails", async () => {
    store.byHarness = { opencode: [] };
    vi.spyOn(store, "create").mockRejectedValue(new Error("This profile isn't valid JSON. EndOfFileExpected at line 2, column 1"));
    const view = mountPanel();
    await flushPromises();

    await view.get("[data-testid='harness-profile-new']").trigger("click");
    await view.get("[data-testid='harness-profile-name']").setValue("Broken");
    await view.get("[data-testid='harness-profile-save']").trigger("click");
    await flushPromises();

    expect(view.get("[data-testid='harness-profile-result']").text()).toContain("EndOfFileExpected at line 2, column 1");
    expect(view.find("[data-testid='harness-profile-editor']").exists()).toBe(true);
  });

  it("asks before deleting, then deletes", async () => {
    store.byHarness = { opencode: [profile("work", "Work")] };
    const remove = vi.spyOn(store, "remove").mockResolvedValue();
    const view = mountPanel();
    await flushPromises();

    await view.get("[data-testid='harness-profile-edit-work']").trigger("click");
    await view.get("[data-testid='harness-profile-delete']").trigger("click");
    expect(remove).not.toHaveBeenCalled();
    expect(view.get("[data-testid='harness-profile-delete']").text()).toBe("Delete Work?");

    await view.get("[data-testid='harness-profile-delete']").trigger("click");
    await flushPromises();
    expect(remove).toHaveBeenCalledWith("opencode", "work");
  });
});
