import { flushPromises, mount } from "@vue/test-utils";
import { afterEach, describe, expect, it, vi } from "vitest";
import { reactive } from "vue";

const store = vi.hoisted(() => ({ state: null as unknown as { isSwitchedOn: boolean; loadSwitch: ReturnType<typeof vi.fn> } }));
vi.mock("@/stores/mods", () => ({ useModsStore: () => store.state }));
const stub = (name: string) => ({ template: `<div data-testid="${name}-pane" />` });
vi.mock("@/components/settings/SkillsSection.vue", () => ({ default: stub("skills") }));
vi.mock("@/components/settings/ToolsSection.vue", () => ({ default: stub("tools") }));
vi.mock("@/components/settings/ModsSection.vue", () => ({ default: stub("mods") }));

const { default: SkillsToolsPage } = await import("@/components/settings/SkillsToolsPage.vue");
const { useSettingsNav } = await import("@/composables/use-settings-nav");

async function mountPage(modsOn: boolean) {
  store.state = reactive({ isSwitchedOn: modsOn, loadSwitch: vi.fn().mockResolvedValue(undefined) });
  const wrapper = mount(SkillsToolsPage, { attachTo: document.body });
  await flushPromises();
  return wrapper;
}

afterEach(() => { document.body.innerHTML = ""; });

describe("Skills & tools", () => {
  it("has a Mods tab only while the Mods switch is on", async () => {
    useSettingsNav().setActiveSection("skills");
    expect((await mountPage(false)).findAll("[role=tab]").map((tab) => tab.text())).toEqual(["Skills", "Tools"]);
    expect((await mountPage(true)).findAll("[role=tab]").map((tab) => tab.text())).toEqual(["Skills", "Tools", "Mods"]);
  });

  it("opens the Tools tab for the old tools id and the Mods tab for the old mods id", async () => {
    useSettingsNav().setActiveSection("tools");
    expect((await mountPage(true)).find("[data-testid=tools-pane]").exists()).toBe(true);
    useSettingsNav().setActiveSection("mods");
    expect((await mountPage(true)).find("[data-testid=mods-pane]").exists()).toBe(true);
  });

  it("falls back to Skills when the old mods id is used with Mods off", async () => {
    useSettingsNav().setActiveSection("mods");
    const wrapper = await mountPage(false);
    expect(wrapper.find("[data-testid=skills-pane]").exists()).toBe(true);
    expect(wrapper.find("[data-testid=mods-pane]").exists()).toBe(false);
  });
});
