import { config, flushPromises, mount } from "@vue/test-utils";
import { createPinia, getActivePinia, setActivePinia } from "pinia";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent, h } from "vue";
import { saveMachines, setActiveMachine } from "@/lib/machines";
import { resetSettingsMachine } from "@/composables/use-settings-machine";
import { useMachinesStore } from "@/stores/machines";
import SettingsPage from "@/components/settings/SettingsPage.vue";
import { useSettingsNav, type SettingsSectionId } from "@/composables/use-settings-nav";

// The page's own job is the header and the scroll; the sections are stubbed down to the groups the anchors name.
const group = (name: string, id: string) => defineComponent({ name, render: () => h("div", { "data-setting-group": id }) });
const stubs = {
  WorkspaceSection: true,
  WorktreeNamingSection: true,
  StorageSection: true,
  SkillsToolsPage: group("SkillsToolsPage", "tools"),
  PermissionsSection: group("PermissionsSection", "browser"),
};

const scrollIntoView = vi.fn();

describe("SettingsPage", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    scrollIntoView.mockReset();
    Element.prototype.scrollIntoView = scrollIntoView;
    // jsdom has no CSS.escape; the ids used here need none.
    vi.stubGlobal("CSS", { escape: (value: string) => value });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    useSettingsNav().setActiveSection("general");
  });

  async function mountAt(section: SettingsSectionId, anchor: string | null) {
    useSettingsNav().setActiveSection(section, anchor);
    const wrapper = mount(SettingsPage, { attachTo: document.body, global: { stubs } });
    await flushPromises();
    return wrapper;
  }

  it("scrolls to a group once and then lets the anchor go, so coming back doesn't scroll again", async () => {
    const wrapper = await mountAt("folders", "storage");

    expect(scrollIntoView).toHaveBeenCalledTimes(1);
    expect(useSettingsNav().activeAnchor.value).toBeNull();
    wrapper.unmount();
  });

  it("lets go of the Permissions › Browser anchor too", async () => {
    const wrapper = await mountAt("permissions", "browser");

    expect(scrollIntoView).toHaveBeenCalledTimes(1);
    expect(useSettingsNav().activeAnchor.value).toBeNull();
    wrapper.unmount();
  });

  it("keeps the anchor on Skills & tools, which picks its tab from it", async () => {
    const wrapper = await mountAt("skills", "tools");

    expect(scrollIntoView).toHaveBeenCalled();
    expect(useSettingsNav().activeAnchor.value).toBe("tools");
    wrapper.unmount();
  });

  it("keeps an anchor whose group isn't on the page yet", async () => {
    const wrapper = await mountAt("folders", "not-there-yet");

    expect(scrollIntoView).not.toHaveBeenCalled();
    expect(useSettingsNav().activeAnchor.value).toBe("not-there-yet");
    wrapper.unmount();
  });

  it("names the picked machine on General and Labs, and This browser on Appearance", async () => {
    const falcon = { id: "ffffffffffffffffffffffffffffffff", name: "falcon", baseUrl: "http://falcon.example.test:2113", token: "falcon-token-0123456789", os: "linux", addedAt: "2026-11-07T00:00:00.000Z" };
    config.global.plugins = [getActivePinia()!];
    localStorage.clear();
    setActiveMachine(null);
    saveMachines([falcon]);
    vi.stubGlobal("fetch", vi.fn(async () => Response.json([])));
    useMachinesStore().setCurrent(falcon.id);
    resetSettingsMachine();
    const wrapper = await mountAt("general", null);

    expect(wrapper.get("[data-testid='settings-machine-chip']").text()).toBe("falcon");
    expect(wrapper.find("[data-testid='general-section']").exists()).toBe(true);
    useSettingsNav().setActiveSection("labs");
    await flushPromises();
    expect(wrapper.get("[data-testid='settings-machine-chip']").text()).toBe("falcon");
    expect(wrapper.find("[data-testid='labs-section']").exists()).toBe(true);
    useSettingsNav().setActiveSection("appearance");
    await flushPromises();
    expect(wrapper.get("[data-testid='settings-machine-chip']").text()).toBe("This browser");
    wrapper.unmount();
  });
});
