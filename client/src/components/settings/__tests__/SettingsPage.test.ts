import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent, h } from "vue";
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
});
