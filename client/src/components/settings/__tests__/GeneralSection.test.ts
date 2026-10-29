import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import GeneralSection from "@/components/settings/GeneralSection.vue";

const { getMock, putMock } = vi.hoisted(() => ({ getMock: vi.fn(), putMock: vi.fn() }));

vi.mock("@/api/client", () => ({ api: { GET: getMock, PUT: putMock } }));

async function mountSection() {
  getMock.mockResolvedValue({ data: {} });
  putMock.mockResolvedValue({});
  const wrapper = mount(GeneralSection);
  await flushPromises();
  return wrapper;
}

describe("GeneralSection", () => {
  beforeEach(() => {
    localStorage.clear();
    setActivePinia(createPinia());
    getMock.mockReset();
    putMock.mockReset();
    // An older home with no machine list: this browser's own list is the one shown.
    vi.stubGlobal("fetch", vi.fn(async () => new Response("{}", { status: 404 })));
  });

  afterEach(() => vi.unstubAllGlobals());

  it("shows starters by default, and saves turning them off", async () => {
    const wrapper = await mountSection();
    const toggle = wrapper.get("[data-testid='starters-switch']");
    expect(toggle.attributes("aria-checked")).toBe("true");

    await toggle.trigger("click");
    await flushPromises();

    expect(putMock).toHaveBeenCalledWith("/api/preferences/{key}", { params: { path: { key: "NewSessionStarters" } }, body: { value: "false" } });
    expect(toggle.attributes("aria-checked")).toBe("false");
  });

  it("has no Usage limits switch, and a status bar switch that is on until turned off", async () => {
    const wrapper = await mountSection();
    expect(wrapper.find("[data-testid='usage-limits-switch']").exists()).toBe(false);
    const chip = wrapper.get("[data-testid='usage-limits-chip-switch']");
    expect(chip.attributes("aria-checked")).toBe("true");

    await chip.trigger("click");
    await flushPromises();
    expect(putMock).toHaveBeenLastCalledWith("/api/preferences/{key}", { params: { path: { key: "UsageLimitsChip" } }, body: { value: "false" } });
  });

  describe("Large pastes", () => {
    it("always shows, and saves the choice", async () => {
      const wrapper = await mountSection();
      const select = wrapper.get("[data-testid='large-pastes-select']");
      expect((select.element as HTMLSelectElement).value).toBe("ask");
      expect(wrapper.text()).toContain("When you paste 2,000 characters or 25 lines or more.");

      await select.setValue("attach");
      await flushPromises();

      expect(putMock).toHaveBeenCalledWith("/api/preferences/{key}", { params: { path: { key: "LargePastes" } }, body: { value: "attach" } });
      expect((select.element as HTMLSelectElement).value).toBe("attach");
    });
  });

  it("groups the everyday settings, and keeps each full text behind More", async () => {
    const wrapper = await mountSection();
    const groups = wrapper.findAll("[data-setting-group]").map((group) => group.attributes("data-setting-group"));
    expect(groups).toEqual(["new-sessions", "away", "rail"]);
    expect(wrapper.text()).not.toContain("nothing is sent until you press Enter");

    await wrapper.get("[data-testid='setting-more-starters']").trigger("click");

    expect(wrapper.get("[data-testid='setting-more-text-starters']").text()).toContain("nothing is sent until you press Enter");
  });
});
