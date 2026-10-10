import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import FeaturesSection from "@/components/settings/FeaturesSection.vue";
import { saveMachines } from "@/lib/machines";

const { getMock, putMock } = vi.hoisted(() => ({ getMock: vi.fn(), putMock: vi.fn() }));

vi.mock("@/api/client", () => ({ api: { GET: getMock, PUT: putMock } }));

async function mountSection() {
  getMock.mockResolvedValue({ data: {} });
  putMock.mockResolvedValue({});
  const wrapper = mount(FeaturesSection);
  await flushPromises();
  return wrapper;
}

describe("FeaturesSection", () => {
  beforeEach(() => {
    localStorage.clear();
    setActivePinia(createPinia());
    getMock.mockReset();
    putMock.mockReset();
    // An older home with no machine list: this browser's own list is the one shown.
    vi.stubGlobal("fetch", vi.fn(async () => new Response("{}", { status: 404 })));
  });

  afterEach(() => vi.unstubAllGlobals());

  it("doesn't offer to keep machines live with only this one", async () => {
    const wrapper = await mountSection();

    expect(wrapper.find("[data-testid='live-machines-switch']").exists()).toBe(false);
  });

  it("offers it, off, once there's another machine, and saves the choice", async () => {
    saveMachines([{ id: "m1", name: "mini", baseUrl: "http://mini.example.test:2113", token: "mini-token", addedAt: "2026-10-08T00:00:00.000Z" }]);
    const wrapper = await mountSection();
    const toggle = wrapper.get("[data-testid='live-machines-switch']");
    expect(toggle.attributes("aria-checked")).toBe("false");

    await toggle.trigger("click");
    await flushPromises();

    expect(putMock).toHaveBeenCalledWith("/api/preferences/{key}", { params: { path: { key: "LiveMachines" } }, body: { value: "true" } });
    expect(toggle.attributes("aria-checked")).toBe("true");
  });

  describe("Mods", () => {
    const description = "Agents can write small add-ons that draw in Fleet: counts on tool rows, a band above the composer, a status-bar chip. You review each one before it's kept.";

    it("is off by default, labelled Experimental, with its description", async () => {
      const wrapper = await mountSection();
      const toggle = wrapper.get("[data-testid='mods-switch']");

      expect(toggle.attributes("aria-checked")).toBe("false");
      expect(toggle.attributes("role")).toBe("switch");
      const row = toggle.element.closest("div.rounded-card") as HTMLElement;
      expect(row.textContent).toContain("Mods");
      expect(row.textContent).toContain("Experimental");
      expect(row.textContent).toContain(description);
    });

    it("saves Mods true, then false", async () => {
      const wrapper = await mountSection();
      const toggle = wrapper.get("[data-testid='mods-switch']");

      await toggle.trigger("click");
      await flushPromises();
      expect(putMock).toHaveBeenLastCalledWith("/api/preferences/{key}", { params: { path: { key: "Mods" } }, body: { value: "true" } });
      expect(toggle.attributes("aria-checked")).toBe("true");

      await toggle.trigger("click");
      await flushPromises();
      expect(putMock).toHaveBeenLastCalledWith("/api/preferences/{key}", { params: { path: { key: "Mods" } }, body: { value: "false" } });
      expect(toggle.attributes("aria-checked")).toBe("false");
    });

    it("shows the server's value when the preference isn't set", async () => {
      vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ on: true, safeMode: false }), { status: 200 })));
      const wrapper = await mountSection();

      expect(wrapper.get("[data-testid='mods-switch']").attributes("aria-checked")).toBe("true");
    });

    it("shows the preference when it is set, whatever the server says", async () => {
      vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ on: true, safeMode: false }), { status: 200 })));
      getMock.mockResolvedValue({ data: { Mods: "false" } });
      putMock.mockResolvedValue({});
      const wrapper = mount(FeaturesSection);
      await flushPromises();

      expect(wrapper.get("[data-testid='mods-switch']").attributes("aria-checked")).toBe("false");
    });

    it("saves the opposite of the server's value when toggled without a preference", async () => {
      vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ on: true, safeMode: false }), { status: 200 })));
      const wrapper = await mountSection();

      await wrapper.get("[data-testid='mods-switch']").trigger("click");
      await flushPromises();

      expect(putMock).toHaveBeenLastCalledWith("/api/preferences/{key}", { params: { path: { key: "Mods" } }, body: { value: "false" } });
    });

    it("is disabled while preferences load", async () => {
      getMock.mockReturnValue(new Promise(() => {}));
      const wrapper = mount(FeaturesSection);
      await flushPromises();

      expect(wrapper.get("[data-testid='mods-switch']").attributes("disabled")).toBeDefined();
    });
  });
});
