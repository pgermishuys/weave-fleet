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
});
